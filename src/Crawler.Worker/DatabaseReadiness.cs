using Crawler.Core.Application.Options;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Worker;

/// <summary>
/// The API owns migrations; the worker waits until the database is reachable and fully migrated
/// before touching it, so a fresh `docker compose up` does not race the schema.
/// </summary>
public sealed class DatabaseReadiness(
    IServiceScopeFactory scopeFactory,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<DatabaseReadiness> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (!_ready)
            {
                var problem = await CheckAsync(cancellationToken);
                if (problem is null)
                {
                    _ready = true;
                    logger.LogInformation("Database is reachable and migrated");
                    break;
                }

                logger.LogWarning("Waiting for the database: {Problem}", problem);
                await Task.Delay(TimeSpan.FromSeconds(options.Value.Worker.DatabaseReadinessPollSeconds), clock, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return "not reachable";
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0 ? null : $"{pending.Count} pending migrations (applied by the API)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }
}
