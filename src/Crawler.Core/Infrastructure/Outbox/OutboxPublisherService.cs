using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Infrastructure.Outbox;

public sealed record OutboxBatchResult(int Selected, int Published, int Failed);

/// <summary>
/// Drains <c>outbox_messages</c> to RabbitMQ. Rows are locked with <c>FOR UPDATE SKIP LOCKED</c> so
/// several API replicas can run the publisher without publishing the same row in the same round.
/// Delivery is at-least-once: a crash between broker confirm and commit republishes the row, which
/// the worker tolerates through its idempotent claim.
/// </summary>
public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    OutboxSignal signal,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    // Keeps Math.Pow finite for any multiplier the options allow; the result is capped by MaxBackoffSeconds anyway.
    private const int MaxBackoffExponent = 16;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outbox = options.Value.Outbox;
        var pollInterval = TimeSpan.FromSeconds(outbox.PollIntervalSeconds);
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            OutboxBatchResult? result;
            try
            {
                result = await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Typically the database is unavailable. The outbox must never take the host down.
                logger.LogWarning(ex, "Outbox publishing round failed");
                result = null;
            }

            try
            {
                if (result is null || result.Failed > 0)
                {
                    consecutiveFailures++;
                    var backoff = Backoff(outbox, consecutiveFailures);
                    logger.LogWarning("Outbox publishing failed; next round in {BackoffSeconds}s (consecutive failures: {ConsecutiveFailures})",
                        backoff.TotalSeconds, consecutiveFailures);
                    await Task.Delay(backoff, clock, stoppingToken);
                    continue;
                }

                consecutiveFailures = 0;
                if (result.Selected >= outbox.BatchSize)
                {
                    // A full batch means more rows are probably waiting; drain without sleeping.
                    continue;
                }

                await signal.WaitAsync(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Poll interval × BackoffMultiplier per consecutive failed round, capped at MaxBackoffSeconds.</summary>
    internal static TimeSpan Backoff(OutboxOptions outbox, int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, MaxBackoffExponent);
        var seconds = outbox.PollIntervalSeconds * Math.Pow(outbox.BackoffMultiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, outbox.MaxBackoffSeconds));
    }

    /// <summary>One round: lock a batch, publish in creation order, record outcomes, commit.</summary>
    internal async Task<OutboxBatchResult> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IJobMessagePublisher>();
        var batchSize = options.Value.Outbox.BatchSize;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY created_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new OutboxBatchResult(0, 0, 0);
        }

        var published = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            using var rowScope = logger.BeginScope("JobId:{JobId} OutboxMessageId:{OutboxMessageId}", row.JobId, row.Id);

            var message = CrawlJobOutbox.TryReadPayload(row);
            if (message is null)
            {
                // A corrupt row can never be published; leaving it unpublished would block the batch forever
                // (rows are ordered by created_at). Retire it and fail its job so the user sees why nothing happens.
                RecordFailure(row, $"Unreadable {row.MessageType} payload; row retired and job failed");
                row.PublishedAt = clock.GetUtcNow();
                var stateStore = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
                await stateStore.TryFailAsync(row.JobId, owner: null, "Job request could not be published (corrupt outbox payload)", cancellationToken);
                logger.LogError("Outbox row {OutboxMessageId} for job {JobId} has an unreadable payload; retired", row.Id, row.JobId);
                continue;
            }

            try
            {
                await publisher.PublishAsync(message, RabbitMqTopology.FirstAttempt, JobQueue.Main, cancellationToken);
                row.PublishedAt = clock.GetUtcNow();
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                RecordFailure(row, ex.Message);
                failed++;
                logger.LogWarning(ex, "Publishing outbox row {OutboxMessageId} for job {JobId} failed (attempt {PublishAttempts})",
                    row.Id, row.JobId, row.PublishAttempts);

                // A publish failure almost always means the broker is unreachable; the remaining rows
                // would fail the same way, each waiting for a connection attempt while holding row locks.
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OutboxBatchResult(rows.Count, published, failed);
    }

    private static void RecordFailure(OutboxMessage row, string error)
    {
        row.PublishAttempts++;
        row.LastError = error.Length <= CrawlerDbContext.ErrorTextMaxLength ? error : error[..CrawlerDbContext.ErrorTextMaxLength];
    }
}
