using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Recovery;

/// <summary>
/// Republishes jobs whose worker stopped renewing its lease (crash, kill, network partition). This is
/// what makes acknowledging a duplicate of an actively leased job safe (D2).
/// </summary>
public sealed class LeaseRecoveryService(
    IServiceScopeFactory scopeFactory,
    IJobMessagePublisher publisher,
    DatabaseReadiness databaseReadiness,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<LeaseRecoveryService> logger) : BackgroundService
{
    public const int SweepBatchSize = 50;
    private static readonly TimeSpan RepublishCooldown = TimeSpan.FromMinutes(5);
    private readonly Dictionary<Guid, (DateTimeOffset? LeaseExpiresAt, DateTimeOffset PublishedAt)> _recentlyRepublished = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await databaseReadiness.WaitUntilReadyAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.Worker.RecoverySweepSeconds), clock);
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
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var stateStore = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
            expired = await stateStore.FindExpiredLeasesAsync(SweepBatchSize, cancellationToken);
        }

        foreach (var stale in _recentlyRepublished.Where(e => clock.GetUtcNow() - e.Value.PublishedAt >= RepublishCooldown).Select(e => e.Key).ToList())
        {
            _recentlyRepublished.Remove(stale);
        }

        var republished = 0;
        foreach (var lease in expired)
        {
            // While nobody claims the job (all consumers busy), every sweep would add another copy. One copy per
            // expired lease per cooldown window is enough; duplicates are acknowledged harmlessly anyway.
            if (_recentlyRepublished.TryGetValue(lease.JobId, out var recent)
                && recent.LeaseExpiresAt == lease.LeaseExpiresAt
                && clock.GetUtcNow() - recent.PublishedAt < RepublishCooldown)
            {
                continue;
            }

            try
            {
                var message = CrawlJobMessage.For(lease.JobId, lease.CorrelationId, clock.GetUtcNow());
                await publisher.PublishAsync(message, attempt: 1, JobQueue.Main, cancellationToken);
                _recentlyRepublished[lease.JobId] = (lease.LeaseExpiresAt, clock.GetUtcNow());
                republished++;
                logger.LogWarning("Lease expired for job {JobId} (owner {LeaseOwner}); republished", lease.JobId, lease.LeaseOwner);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not republish job {JobId} with an expired lease", lease.JobId);
            }
        }

        return republished;
    }
}
