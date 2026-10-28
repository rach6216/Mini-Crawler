namespace Crawler.Core.Domain;

public sealed class CrawlPage
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string NormalizedUrl { get; set; } = string.Empty;
    public int Depth { get; set; }
    public PageFetchStatus FetchStatus { get; set; }
    public int? HttpStatusCode { get; set; }
    public double DomainLinkRatio { get; set; }
    public int OutgoingLinkCount { get; set; }
    public int InternalLinkCount { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? ParentPageId { get; set; }
    public int DiscoveryOrder { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }

    public static CrawlPage CreateRoot(Guid jobId, Uri normalizedUrl) =>
        new()
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            NormalizedUrl = normalizedUrl.AbsoluteUri,
            Depth = 0,
            FetchStatus = PageFetchStatus.Discovered,
            DiscoveryOrder = 0,
        };

    public static CrawlPage CreateChild(Guid jobId, Uri normalizedUrl, int depth, Guid parentPageId, int discoveryOrder) =>
        new()
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            NormalizedUrl = normalizedUrl.AbsoluteUri,
            Depth = depth,
            FetchStatus = PageFetchStatus.Discovered,
            ParentPageId = parentPageId,
            DiscoveryOrder = discoveryOrder,
        };

    public void MarkFetched(int httpStatusCode, LinkMetrics metrics, DateTimeOffset now)
    {
        FetchStatus = PageFetchStatus.Fetched;
        HttpStatusCode = httpStatusCode;
        OutgoingLinkCount = metrics.TotalLinks;
        InternalLinkCount = metrics.InternalLinks;
        DomainLinkRatio = metrics.Ratio;
        ErrorMessage = null;
        FetchedAt = now;
    }

    public void MarkFailed(int? httpStatusCode, string error, DateTimeOffset now)
    {
        FetchStatus = PageFetchStatus.Failed;
        HttpStatusCode = httpStatusCode;
        ErrorMessage = error;
        FetchedAt = now;
    }

    public void MarkSkipped(int? httpStatusCode, string reason, DateTimeOffset now)
    {
        FetchStatus = PageFetchStatus.Skipped;
        HttpStatusCode = httpStatusCode;
        ErrorMessage = reason;
        FetchedAt = now;
    }
}
