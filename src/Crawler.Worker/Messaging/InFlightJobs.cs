using System.Collections.Concurrent;

namespace Crawler.Worker.Messaging;

/// <summary>Jobs currently being handled by this process (at most one handler per job).</summary>
public sealed class InFlightJobs
{
    private readonly ConcurrentDictionary<Guid, byte> _jobs = new();

    public bool TryEnter(Guid jobId) => _jobs.TryAdd(jobId, 0);

    public void Exit(Guid jobId) => _jobs.TryRemove(jobId, out _);
}
