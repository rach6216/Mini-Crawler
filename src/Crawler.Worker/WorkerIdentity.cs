namespace Crawler.Worker;

/// <summary>Lease owner id of this worker process; unique per process start.</summary>
public sealed record WorkerIdentity(string OwnerId)
{
    public static WorkerIdentity CreateForProcess() =>
        new($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}
