namespace Crawler.Core.Domain;

public sealed class OutboxMessage
{
    public const string CrawlJobRequestedType = "CrawlJobRequested";

    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int PublishAttempts { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage CrawlJobRequested(Guid jobId, string payload, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            MessageType = CrawlJobRequestedType,
            Payload = payload,
            CreatedAt = now,
        };
}
