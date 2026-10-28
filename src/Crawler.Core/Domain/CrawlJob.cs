namespace Crawler.Core.Domain;

public sealed class CrawlJob
{
    public Guid Id { get; set; }
    public string InputUrl { get; set; } = string.Empty;
    public string NormalizedStartUrl { get; set; } = string.Empty;
    public string StartingHost { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public int MaxDepth { get; set; }
    public int MaxPages { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int DiscoveredPages { get; set; }
    public int ProcessedPages { get; set; }
    public int FailedPages { get; set; }
    public bool Truncated { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public Guid CorrelationId { get; set; }

    public ICollection<CrawlPage> Pages { get; set; } = new List<CrawlPage>();

    public static CrawlJob Create(string inputUrl, Uri normalizedStartUrl, int maxDepth, int maxPages, DateTimeOffset now)
    {
        return new CrawlJob
        {
            Id = Guid.NewGuid(),
            InputUrl = inputUrl,
            NormalizedStartUrl = normalizedStartUrl.AbsoluteUri,
            StartingHost = normalizedStartUrl.Host,
            Status = JobStatus.Pending,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            CreatedAt = now,
            CorrelationId = Guid.NewGuid(),
        };
    }

    public bool IsTerminal => JobStateMachine.IsTerminal(Status);
}
