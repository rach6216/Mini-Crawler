namespace Crawler.Worker.Crawling;

public enum CrawlOutcome
{
    Completed,
    Canceled,
}

/// <summary>A conditional write found that this worker no longer owns the job; all work must stop.</summary>
public sealed class LeaseLostException(Guid jobId, string operation)
    : Exception($"Lease for job {jobId} was lost during {operation}")
{
    public Guid JobId { get; } = jobId;
}
