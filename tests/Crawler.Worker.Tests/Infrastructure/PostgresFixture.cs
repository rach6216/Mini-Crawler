using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>
/// Real PostgreSQL for integration tests: <c>CRAWLER_TEST_PG</c> when set, otherwise a Testcontainer.
/// Tests isolate themselves by creating their own jobs, so one migrated database serves the collection.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("CRAWLER_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("crawler")
                .WithUsername("crawler")
                .WithPassword("crawler")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public CrawlerDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CrawlerDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
