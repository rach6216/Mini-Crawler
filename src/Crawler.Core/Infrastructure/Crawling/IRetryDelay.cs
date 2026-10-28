namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>Seam over the backoff wait so retry tests can assert delays without sleeping.</summary>
public interface IRetryDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TimeProviderRetryDelay(TimeProvider clock) : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, clock, cancellationToken);
}
