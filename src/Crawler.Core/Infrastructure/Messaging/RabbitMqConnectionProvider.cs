using Crawler.Core.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Core.Infrastructure.Messaging;

/// <summary>
/// Lazily opens one shared connection per process and declares the topology on it once. Channels
/// are cheap and created per operation by callers.
/// </summary>
public sealed class RabbitMqConnectionProvider(
    IOptions<RabbitMqOptions> rabbitOptions,
    IOptions<CrawlerOptions> crawlerOptions,
    ILogger<RabbitMqConnectionProvider> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _topologyDeclared;

    public bool IsConnected => _connection is { IsOpen: true };

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _topologyDeclared)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                // Drop a closed connection explicitly so a recovering one cannot linger next to its replacement.
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                }

                var options = rabbitOptions.Value;
                var factory = new ConnectionFactory
                {
                    HostName = options.Host,
                    Port = options.Port,
                    UserName = options.UserName,
                    Password = options.Password,
                    VirtualHost = options.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    ClientProvidedName = $"crawler-{Environment.MachineName}-{Environment.ProcessId}",
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _topologyDeclared = false;
                logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", options.Host, options.Port);
            }

            if (!_topologyDeclared)
            {
                await using var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
                var retryDelay = TimeSpan.FromSeconds(crawlerOptions.Value.Worker.RetryDelaySeconds);
                await RabbitMqTopology.DeclareAsync(channel, retryDelay, cancellationToken);
                _topologyDeclared = true;
            }

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var options = publisherConfirms
            ? new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)
            : null;
        return await connection.CreateChannelAsync(options, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
