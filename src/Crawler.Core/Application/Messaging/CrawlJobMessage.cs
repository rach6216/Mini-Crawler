namespace Crawler.Core.Application.Messaging;

/// <summary>
/// Broker message that asks a worker to process a job. Carries identity only; the job row is the
/// authoritative source for crawl parameters and mutable state.
/// </summary>
public sealed record CrawlJobMessage(int SchemaVersion, Guid MessageId, Guid JobId, Guid CorrelationId, DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static CrawlJobMessage For(Guid jobId, Guid correlationId, DateTimeOffset now) =>
        new(CurrentSchemaVersion, Guid.NewGuid(), jobId, correlationId, now);
}

public enum JobQueue
{
    Main,
    Retry,
}

public interface IJobMessagePublisher
{
    /// <param name="attempt">1-based processing attempt carried in the <c>x-attempt</c> header.</param>
    Task PublishAsync(CrawlJobMessage message, int attempt, JobQueue queue, CancellationToken cancellationToken);
}
