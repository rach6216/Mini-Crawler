using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Core.Infrastructure.Persistence;

/// <summary>
/// All job state transitions as conditional UPDATEs. The WHERE clause carries the expected source
/// status (and lease owner for worker-driven changes), so concurrent workers, duplicate deliveries and
/// late cancellations can never overwrite a terminal state or each other's progress.
/// </summary>
public sealed class JobStateStore(CrawlerDbContext db, TimeProvider clock) : IJobStateStore
{
    public async Task<bool> TryClaimAsync(Guid jobId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseExpiresAt = now + leaseDuration;

        // A worker may re-claim a job it already owns (its own retry copy arrived while the lease is
        // still active); that simply extends the lease instead of waiting for it to expire.
        var affected = await db.Jobs
            .Where(j => j.Id == jobId)
            .Where(j => j.Status == JobStatus.Pending
                        || (j.Status == JobStatus.Running
                            && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now || j.LeaseOwner == owner)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                .SetProperty(j => j.LeaseOwner, owner)
                .SetProperty(j => j.LeaseExpiresAt, leaseExpiresAt)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var leaseExpiresAt = clock.GetUtcNow() + leaseDuration;

        var affected = await OwnedRunningJob(jobId, owner)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.LeaseExpiresAt, leaseExpiresAt), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryApplyProgressAsync(Guid jobId, string owner, PageProgressDelta delta, CancellationToken cancellationToken)
    {
        var affected = await OwnedRunningJob(jobId, owner)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.DiscoveredPages, j => j.DiscoveredPages + delta.DiscoveredDelta)
                .SetProperty(j => j.ProcessedPages, j => j.ProcessedPages + delta.ProcessedDelta)
                .SetProperty(j => j.FailedPages, j => j.FailedPages + delta.FailedDelta)
                .SetProperty(j => j.Truncated, j => j.Truncated || delta.Truncated), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryCompleteAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var affected = await OwnedRunningJob(jobId, owner)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Completed)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryFailAsync(Guid jobId, string? owner, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var affected = await db.Jobs
            .Where(j => j.Id == jobId)
            .Where(j => j.Status == JobStatus.Pending
                        || (owner != null && j.Status == JobStatus.Running && j.LeaseOwner == owner))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.FailureReason, reason)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryCancelRunningAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var affected = await OwnedRunningJob(jobId, owner)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        return affected == 1;
    }

    public async Task<bool> TryFailExhaustedAsync(Guid jobId, int maxAttempts, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var affected = await db.Jobs
            .Where(j => j.Id == jobId && j.Attempts >= maxAttempts)
            .Where(j => j.Status == JobStatus.Pending
                        || (j.Status == JobStatus.Running && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.FailureReason, reason)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);

        return affected == 1;
    }

    public async Task<CancellationRequestOutcome> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // A Pending job has no worker to observe the flag, so it becomes Canceled immediately.
        var canceledNow = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.CancellationRequested, true)
                .SetProperty(j => j.CancellationRequestedAt, now), cancellationToken);
        if (canceledNow == 1)
        {
            return CancellationRequestOutcome.Canceled;
        }

        var flagged = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Running && !j.CancellationRequested)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.CancellationRequested, true)
                .SetProperty(j => j.CancellationRequestedAt, now), cancellationToken);
        if (flagged == 1)
        {
            return CancellationRequestOutcome.Requested;
        }

        var current = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Status, j.CancellationRequested })
            .SingleOrDefaultAsync(cancellationToken);

        return current switch
        {
            null => CancellationRequestOutcome.NotFound,
            { Status: JobStatus.Canceled } => CancellationRequestOutcome.AlreadyCanceled,
            { Status: JobStatus.Running, CancellationRequested: true } => CancellationRequestOutcome.AlreadyRequested,
            _ => CancellationRequestOutcome.AlreadyTerminal,
        };
    }

    public Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.CancellationRequested)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ExpiredLease>> FindExpiredLeasesAsync(int limit, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        return await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Running && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .OrderBy(j => j.LeaseExpiresAt)
            .Take(limit)
            .Select(j => new ExpiredLease(j.Id, j.CorrelationId, j.LeaseOwner, j.LeaseExpiresAt))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<CrawlJob> OwnedRunningJob(Guid jobId, string owner) =>
        db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running && j.LeaseOwner == owner);
}
