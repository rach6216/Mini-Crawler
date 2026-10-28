using System.Threading.Channels;

namespace Crawler.Core.Infrastructure.Outbox;

/// <summary>
/// In-process wake-up for the outbox publisher so a freshly committed job is published immediately
/// instead of after the next poll. Notifications coalesce: many calls before a wait produce one wake-up.
/// </summary>
public sealed class OutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <returns><c>true</c> when woken by <see cref="Notify"/>, <c>false</c> when the timeout elapsed.</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _))
        {
            return true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
