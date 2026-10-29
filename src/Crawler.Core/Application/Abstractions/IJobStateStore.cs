using Crawler.Core.Domain;

namespace Crawler.Core.Application.Abstractions;

public enum CancellationRequestOutcome
{
    NotFound,
    /// <summary>Job was Pending and is now Canceled.</summary>
    Canceled,
    /// <summary>Job is Running; the flag is set and the worker will stop at the next boundary.</summary>
    Requested,
    AlreadyRequested,
    AlreadyCanceled,
    /// <summary>Job is Completed or Failed; cancellation is impossible.</summary>
    AlreadyTerminal,
}

public sealed record ExpiredLease(Guid JobId, Guid CorrelationId, string? LeaseOwner, DateTimeOffset? LeaseExpiresAt);

public sealed record StalePendingJob(Guid JobId, Guid CorrelationId, DateTimeOffset CreatedAt);

public sealed record PageProgressDelta(int DiscoveredDelta, int ProcessedDelta, int FailedDelta, bool Truncated);

/// <summary>
/// Every method is a single conditional UPDATE; a <c>false</c> result means the precondition
/// (expected status and, for worker operations, lease ownership) no longer holds.
/// </summary>
public interface IJobStateStore
{
    Task<bool> TryClaimAsync(Guid jobId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<bool> RenewLeaseAsync(Guid jobId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Applies page counters to the job. Must be called inside the caller's transaction on the same
    /// DbContext scope so that page/edge inserts and the counters commit atomically.
    /// </summary>
    Task<bool> TryApplyProgressAsync(Guid jobId, string owner, PageProgressDelta delta, CancellationToken cancellationToken);

    Task<bool> TryCompleteAsync(Guid jobId, string owner, CancellationToken cancellationToken);

    /// <param name="owner">
    /// When null, only a Pending job can be failed (message exhausted before any claim).
    /// When set, a Running job owned by <paramref name="owner"/> can be failed as well.
    /// </param>
    Task<bool> TryFailAsync(Guid jobId, string? owner, string reason, CancellationToken cancellationToken);

    Task<bool> TryCancelRunningAsync(Guid jobId, string owner, CancellationToken cancellationToken);

    /// <summary>
    /// Fails a job that has already been claimed <paramref name="maxAttempts"/> times and is claimable again
    /// (Pending, or Running with an expired lease). This bounds retries from the database, so crashes that
    /// bypass the message attempt header (process death, broker redelivery, sweeper republish) still terminate.
    /// </summary>
    Task<bool> TryFailExhaustedAsync(Guid jobId, int maxAttempts, string reason, CancellationToken cancellationToken);

    Task<CancellationRequestOutcome> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken);

    Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpiredLease>> FindExpiredLeasesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Pending jobs created more than <paramref name="olderThan"/> ago without a cancellation request,
    /// oldest first. Such a job's message was lost (queue purged, broker data loss, dead-lettered by a bug).
    /// </summary>
    Task<IReadOnlyList<StalePendingJob>> FindStalePendingAsync(TimeSpan olderThan, int limit, CancellationToken cancellationToken);
}
