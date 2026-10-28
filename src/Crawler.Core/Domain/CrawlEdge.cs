namespace Crawler.Core.Domain;

public sealed class CrawlEdge
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Guid SourcePageId { get; set; }
    public string TargetNormalizedUrl { get; set; } = string.Empty;
    public Guid? TargetPageId { get; set; }
    public int OccurrenceCount { get; set; }
    public bool IsInternal { get; set; }
}
