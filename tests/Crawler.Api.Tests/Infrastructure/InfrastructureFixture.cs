using System.Collections.Concurrent;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Crawler.Api.Tests.Infrastructure;

public sealed record RabbitMqEndpoint(string Host, int Port, string UserName, string Password, string VirtualHost)
{
    /// <summary>Parses <c>amqp://user:password@host:port/vhost</c>.</summary>
    public static RabbitMqEndpoint Parse(string amqpUri)
    {
        var uri = new Uri(amqpUri);
        var credentials = uri.UserInfo.Split(':', 2);
        var vhost = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return new RabbitMqEndpoint(
            uri.Host,
            uri.IsDefaultPort || uri.Port <= 0 ? 5672 : uri.Port,
            Uri.UnescapeDataString(credentials[0]),
            credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
            vhost.Length == 0 ? "/" : vhost);
    }
}

/// <summary>
/// Real PostgreSQL and RabbitMQ for the whole test collection: Testcontainers by default, or existing
/// servers through <c>CRAWLER_TEST_PG</c> (Npgsql connection string) and <c>CRAWLER_TEST_RABBITMQ</c>
/// (AMQP URI). Each test class gets its own database so counts and outbox scans are isolated.
/// </summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:16-alpine";
    private const string RabbitMqImage = "rabbitmq:3.13-management-alpine";

    private readonly ConcurrentDictionary<string, CrawlerApiFactory> _factories = new();
    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbitMq;
    private string _postgresConnectionString = string.Empty;

    public RabbitMqEndpoint RabbitMq { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var externalPostgres = Environment.GetEnvironmentVariable("CRAWLER_TEST_PG");
        var externalRabbit = Environment.GetEnvironmentVariable("CRAWLER_TEST_RABBITMQ");

        var startPostgres = Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(externalPostgres))
        {
            _postgres = new PostgreSqlBuilder(PostgresImage).Build();
            startPostgres = _postgres.StartAsync();
        }

        var startRabbit = Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(externalRabbit))
        {
            _rabbitMq = new RabbitMqBuilder(RabbitMqImage).Build();
            startRabbit = _rabbitMq.StartAsync();
        }

        await Task.WhenAll(startPostgres, startRabbit);

        _postgresConnectionString = _postgres?.GetConnectionString() ?? externalPostgres!;
        RabbitMq = RabbitMqEndpoint.Parse(_rabbitMq?.GetConnectionString() ?? externalRabbit!);
    }

    public string PostgresConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgresConnectionString) { Database = database }.ConnectionString;

    /// <summary>One started API host per database name, shared by the tests that use it.</summary>
    public CrawlerApiFactory Factory(string database, Action<CrawlerApiFactory.Settings>? configure = null) =>
        _factories.GetOrAdd(database, name =>
        {
            var settings = new CrawlerApiFactory.Settings(PostgresConnectionString(name), RabbitMq);
            configure?.Invoke(settings);
            return new CrawlerApiFactory(settings);
        });

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories.Values)
        {
            await factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "infrastructure";
}
