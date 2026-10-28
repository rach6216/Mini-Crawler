using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Core.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Messaging;

/// <summary>Broker-agnostic view of one delivery.</summary>
public sealed record JobDelivery(ReadOnlyMemory<byte> Body, string? MessageId, string? CorrelationId, int Attempt);

public enum MessageDisposition
{
    Ack,
    /// <summary>Reject without requeue so the broker routes the message to the dead-letter queue.</summary>
    DeadLetter,
}

public interface IJobMessageHandler
{
    /// <summary>
    /// Processes one delivery and decides how to settle it. Throws only when the delivery must not be
    /// settled either way (host shutdown, or the retry copy could not be published).
    /// </summary>
    Task<MessageDisposition> HandleAsync(JobDelivery delivery, CancellationToken cancellationToken);
}

public sealed class JobMessageHandler(
    IServiceScopeFactory scopeFactory,
    IJobMessagePublisher publisher,
    WorkerIdentity identity,
    InFlightJobs inFlightJobs,
    IOptions<CrawlerOptions> options,
    ILogger<JobMessageHandler> logger) : IJobMessageHandler
{
    public async Task<MessageDisposition> HandleAsync(JobDelivery delivery, CancellationToken cancellationToken)
    {
        var message = RabbitMqJobMessagePublisher.TryDeserialize(delivery.Body);
        if (message is null)
        {
            logger.LogError("Unparseable job message {MessageId} (attempt {Attempt}) sent to the dead-letter queue: {Body}",
                delivery.MessageId, delivery.Attempt, RabbitMqJobMessagePublisher.Describe(delivery.Body));
            return MessageDisposition.DeadLetter;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = message.MessageId,
            ["JobId"] = message.JobId,
            ["CorrelationId"] = message.CorrelationId,
            ["Attempt"] = delivery.Attempt,
        });

        // The owner id is per process, so a duplicate delivered to this same process would pass the lease check.
        if (!inFlightJobs.TryEnter(message.JobId))
        {
            logger.LogInformation("Job {JobId} is already being processed by this worker; duplicate acknowledged", message.JobId);
            return MessageDisposition.Ack;
        }

        try
        {
            return await ProcessAsync(message, cancellationToken);
        }
        catch (LeaseLostException ex)
        {
            logger.LogWarning("Job {JobId}: {Reason}; another worker owns it now, message acknowledged", message.JobId, ex.Message);
            return MessageDisposition.Ack;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await HandleFailureAsync(message, delivery.Attempt, ex, cancellationToken);
        }
        finally
        {
            inFlightJobs.Exit(message.JobId);
        }
    }

    private async Task<MessageDisposition> ProcessAsync(CrawlJobMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var stateStore = scope.ServiceProvider.GetRequiredService<IJobStateStore>();

        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == message.JobId, cancellationToken);
        if (job is null)
        {
            logger.LogError("Job {JobId} does not exist; message sent to the dead-letter queue", message.JobId);
            return MessageDisposition.DeadLetter;
        }

        if (job.IsTerminal)
        {
            logger.LogInformation("Job {JobId} is already {Status}; message acknowledged", job.Id, job.Status);
            return MessageDisposition.Ack;
        }

        // Database-enforced retry cap: the attempt header cannot count crashes, broker redeliveries or sweeper
        // republishes, but every successful claim increments crawl_jobs.attempts.
        var maxAttempts = options.Value.Worker.MaxMessageAttempts;
        if (job.Attempts >= maxAttempts
            && await stateStore.TryFailExhaustedAsync(job.Id, maxAttempts,
                $"Processing was started {job.Attempts} times without completing (limit {maxAttempts})", cancellationToken))
        {
            logger.LogError("Job {JobId} exceeded {MaxAttempts} processing attempts; marked Failed and message dead-lettered", job.Id, maxAttempts);
            return MessageDisposition.DeadLetter;
        }

        var leaseDuration = TimeSpan.FromSeconds(options.Value.Worker.LeaseSeconds);
        if (!await stateStore.TryClaimAsync(job.Id, identity.OwnerId, leaseDuration, cancellationToken))
        {
            // A lease still held by this process comes from an earlier failed attempt here (InFlightJobs rules
            // out a concurrent one), so the retry copy may continue it instead of waiting for expiry.
            var resumesOwnLease = job.LeaseOwner == identity.OwnerId
                && await stateStore.RenewLeaseAsync(job.Id, identity.OwnerId, leaseDuration, cancellationToken);
            if (!resumesOwnLease)
            {
                logger.LogInformation("Job {JobId} is leased by {LeaseOwner}; duplicate acknowledged (the lease sweeper recovers it if that owner dies)",
                    job.Id, job.LeaseOwner);
                return MessageDisposition.Ack;
            }
        }

        var engine = scope.ServiceProvider.GetRequiredService<ICrawlEngine>();
        await engine.RunAsync(job, identity.OwnerId, cancellationToken);
        return MessageDisposition.Ack;
    }

    private async Task<MessageDisposition> HandleFailureAsync(CrawlJobMessage message, int attempt, Exception error, CancellationToken cancellationToken)
    {
        var maxAttempts = options.Value.Worker.MaxMessageAttempts;
        if (attempt < maxAttempts)
        {
            logger.LogWarning(error, "Transient failure processing job {JobId} on attempt {Attempt}/{MaxAttempts}; scheduling retry",
                message.JobId, attempt, maxAttempts);

            // Publish (with confirms) before the original is acked; if this throws, the original is requeued instead.
            await publisher.PublishAsync(message, attempt + 1, JobQueue.Retry, cancellationToken);
            return MessageDisposition.Ack;
        }

        logger.LogError(error, "Job {JobId} failed on final attempt {Attempt}/{MaxAttempts}; marking it Failed and dead-lettering the message",
            message.JobId, attempt, maxAttempts);

        // Exception text can carry connection strings or internal host names; the details stay in the logs.
        var reason = $"Processing failed after {attempt} attempts ({error.GetType().Name}); see worker logs for details";
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var stateStore = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
            if (!await stateStore.TryFailAsync(message.JobId, identity.OwnerId, reason, cancellationToken))
            {
                logger.LogWarning("Job {JobId} could not be marked Failed (already terminal or owned by another worker)", message.JobId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job stays Running under an expiring lease, so the sweeper will surface it again.
            logger.LogError(ex, "Could not mark job {JobId} Failed", message.JobId);
        }

        return MessageDisposition.DeadLetter;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
