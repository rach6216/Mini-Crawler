using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Recovery;

/// <summary>
/// Republishes jobs that no worker will pick up on its own: Running jobs whose worker stopped renewing its
/// lease (crash, kill, network partition), which is what makes acknowledging a duplicate of an actively leased
/// job safe, and Pending jobs whose message was lost (queue purged, broker data loss, dead-lettered by a bug).
/// </summary>
public sealed class LeaseRecoveryService(
    IServiceScopeFactory scopeFactory,
    IJobMessagePublisher publisher,
    DatabaseReadiness databaseReadiness,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<LeaseRecoveryService> logger) : BackgroundService
{
    // Keyed by job id. LeaseExpiresAt is the expired lease that was recovered, or null for a stale Pending job,
    // so a job that is re-claimed and expires again (a new lease) is not held back by an older entry.
    private readonly Dictionary<Guid, (DateTimeOffset? LeaseExpiresAt, DateTimeOffset PublishedAt)> _recentlyRepublished = new();

    private WorkerOptions Worker => options.Value.Worker;

    private TimeSpan RepublishCooldown => TimeSpan.FromSeconds(Worker.RepublishCooldownSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await databaseReadiness.WaitUntilReadyAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Worker.RecoverySweepSeconds), clock);
            do
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Lease recovery sweep failed; retrying on the next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <returns>Number of jobs republished.</returns>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExpiredLease> expired;
        IReadOnlyList<StalePendingJob> stalePending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var stateStore = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
            expired = await stateStore.FindExpiredLeasesAsync(Worker.RecoverySweepBatchSize, cancellationToken);
            stalePending = await stateStore.FindStalePendingAsync(
                TimeSpan.FromSeconds(Worker.PendingRepublishAfterSeconds), Worker.RecoverySweepBatchSize, cancellationToken);
        }

        foreach (var stale in _recentlyRepublished.Where(e => clock.GetUtcNow() - e.Value.PublishedAt >= RepublishCooldown).Select(e => e.Key).ToList())
        {
            _recentlyRepublished.Remove(stale);
        }

        var republished = 0;
        foreach (var lease in expired)
        {
            if (await TryRepublishAsync(lease.JobId, lease.CorrelationId, lease.LeaseExpiresAt, cancellationToken))
            {
                republished++;
                logger.LogWarning("Lease expired for job {JobId} (owner {LeaseOwner}); republished", lease.JobId, lease.LeaseOwner);
            }
        }

        foreach (var pending in stalePending)
        {
            if (await TryRepublishAsync(pending.JobId, pending.CorrelationId, leaseExpiresAt: null, cancellationToken))
            {
                republished++;
                logger.LogWarning("Job {JobId} still Pending after {Age}; republished", pending.JobId, clock.GetUtcNow() - pending.CreatedAt);
            }
        }

        return republished;
    }

    private async Task<bool> TryRepublishAsync(Guid jobId, Guid correlationId, DateTimeOffset? leaseExpiresAt, CancellationToken cancellationToken)
    {
        // While nobody claims the job (all consumers busy), every sweep would add another copy. One copy per
        // expired lease (or per stale Pending job) per cooldown window is enough; duplicates are acknowledged
        // harmlessly anyway.
        if (_recentlyRepublished.TryGetValue(jobId, out var recent)
            && recent.LeaseExpiresAt == leaseExpiresAt
            && clock.GetUtcNow() - recent.PublishedAt < RepublishCooldown)
        {
            return false;
        }

        try
        {
            var message = CrawlJobMessage.For(jobId, correlationId, clock.GetUtcNow());
            await publisher.PublishAsync(message, attempt: 1, JobQueue.Main, cancellationToken);
            _recentlyRepublished[jobId] = (leaseExpiresAt, clock.GetUtcNow());
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not republish job {JobId}", jobId);
            return false;
        }
    }
}
