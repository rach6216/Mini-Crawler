using System.Collections.Concurrent;
using Crawler.Core.Application.Messaging;

namespace Crawler.Worker.Tests.Infrastructure;

public sealed record PublishedMessage(CrawlJobMessage Message, int Attempt, JobQueue Queue);

public sealed class RecordingPublisher : IJobMessagePublisher
{
    private readonly ConcurrentQueue<PublishedMessage> _published = new();

    public IReadOnlyList<PublishedMessage> Published => [.. _published];

    public Task PublishAsync(CrawlJobMessage message, int attempt, JobQueue queue, CancellationToken cancellationToken)
    {
        _published.Enqueue(new PublishedMessage(message, attempt, queue));
        return Task.CompletedTask;
    }
}

/// <summary>Manually advanced clock; used where lease expiry must be simulated without waiting.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
