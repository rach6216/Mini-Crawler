using System.ComponentModel.DataAnnotations;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Crawler.Api.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public bool MigrateOnStartup { get; set; } = true;
    [Range(1, 300)] public int MigrationMaxAttempts { get; set; } = 30;
    [Range(1, 60)] public int MigrationRetryDelaySeconds { get; set; } = 2;
}

/// <summary>
/// Applies EF migrations before the server starts listening (hosted services registered by the app
/// start before Kestrel). Retries transient connection failures because PostgreSQL may still be
/// starting when the container comes up. EF's execution strategy is deliberately not used: it is
/// incompatible with the explicit transactions in the outbox publisher.
/// </summary>
public sealed class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    TimeProvider clock,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.MigrateOnStartup)
        {
            logger.LogInformation("Database migration on startup is disabled");
            return;
        }

        var delay = TimeSpan.FromSeconds(settings.MigrationRetryDelaySeconds);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database schema is up to date (attempt {Attempt})", attempt);
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < settings.MigrationMaxAttempts)
            {
                logger.LogWarning("Database not reachable yet (attempt {Attempt}/{MaxAttempts}): {Error}. Retrying in {DelaySeconds}s",
                    attempt, settings.MigrationMaxAttempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, clock, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
