using Crawler.Core.Domain;

namespace Crawler.Api.Tests.Infrastructure;

internal static class TestData
{
    /// <summary>PostgreSQL stores microseconds; truncating keeps round-tripped values comparable.</summary>
    public static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % 10));
    }

    public static CrawlJob Job(
        JobStatus status = JobStatus.Pending,
        string url = "https://example.com/",
        DateTimeOffset? createdAt = null,
        string? leaseOwner = null,
        DateTimeOffset? leaseExpiresAt = null)
    {
        var now = createdAt ?? UtcNow();
        var job = CrawlJob.Create(url, new Uri(url), maxDepth: 2, maxPages: 200, now);
        job.Status = status;
        if (status != JobStatus.Pending)
        {
            job.StartedAt = now;
            job.Attempts = 1;
        }

        if (status == JobStatus.Running)
        {
            job.LeaseOwner = leaseOwner ?? "worker-a";
            job.LeaseExpiresAt = leaseExpiresAt ?? now.AddMinutes(2);
        }

        if (JobStateMachine.IsTerminal(status))
        {
            job.CompletedAt = now;
        }

        return job;
    }
}
