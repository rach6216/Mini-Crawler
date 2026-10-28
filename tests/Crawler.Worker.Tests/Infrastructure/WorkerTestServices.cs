using System.Text.Json;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Crawling;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Crawler.Core.Application.Options;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>
/// The worker's real DI graph (AddCrawlerWorkerServices) against the test database, with the network
/// fetcher replaced by <see cref="FixtureSiteFetcher"/>, robots.txt by <see cref="FixtureRobotsPolicyProvider"/>
/// and the broker publisher by a recorder. <see cref="CreateWithHttp"/> keeps the real HTTP fetcher and
/// robots.txt provider instead, for crawls of a <see cref="TestSiteServer"/>.
/// </summary>
public sealed class WorkerTestServices : IAsyncDisposable
{
    public const string StartUrl = "https://example.com/";

    public const string LoopbackHost = "127.0.0.1";

    private readonly FixtureSiteFetcher? _fetcher;

    private WorkerTestServices(ServiceProvider provider, FixtureSiteFetcher? fetcher, RecordingPublisher publisher)
    {
        Provider = provider;
        _fetcher = fetcher;
        Publisher = publisher;
    }

    public ServiceProvider Provider { get; }

    public FixtureSiteFetcher Fetcher => _fetcher ?? throw new InvalidOperationException("This instance uses the real HTTP fetcher.");

    public RecordingPublisher Publisher { get; }

    public string OwnerId => Provider.GetRequiredService<WorkerIdentity>().OwnerId;

    public static WorkerTestServices Create(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        TimeProvider? clock = null) =>
        Build(postgres, settings, clock, realHttp: false);

    /// <summary>Real <c>HttpPageFetcher</c> and <c>RobotsPolicyProvider</c>, loopback trusted, backoff waits skipped.</summary>
    public static WorkerTestServices CreateWithHttp(PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["Crawler:Security:TrustedHosts:0"] = LoopbackHost };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        return Build(postgres, values, clock: null, realHttp: true);
    }

    private static WorkerTestServices Build(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings,
        TimeProvider? clock,
        bool realHttp)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var fetcher = realHttp ? null : new FixtureSiteFetcher();
        var publisher = new RecordingPublisher();

        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddCrawlerWorkerServices(configuration);
        if (fetcher is not null)
        {
            services.RemoveAll<IPageFetcher>();
            services.AddSingleton<IPageFetcher>(fetcher);
            services.RemoveAll<IRobotsPolicyProvider>();
            services.AddSingleton<IRobotsPolicyProvider, FixtureRobotsPolicyProvider>();
        }
        else
        {
            services.RemoveAll<IRetryDelay>();
            services.AddSingleton<IRetryDelay, NoRetryDelay>();
        }

        services.RemoveAll<IJobMessagePublisher>();
        services.AddSingleton<IJobMessagePublisher>(publisher);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return new WorkerTestServices(provider, fetcher, publisher);
    }

    public CrawlerOptions Options => Provider.GetRequiredService<IOptions<CrawlerOptions>>().Value;

    public static async Task<CrawlJob> SeedJobAsync(PostgresFixture postgres, int maxDepth, int maxPages, string startUrl = StartUrl)
    {
        var job = CrawlJob.Create(startUrl, new Uri(startUrl), maxDepth, maxPages, DateTimeOffset.UtcNow);
        await using var db = postgres.CreateDbContext();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    public async Task<bool> ClaimAsync(Guid jobId)
    {
        await using var scope = Provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
        return await store.TryClaimAsync(jobId, OwnerId, TimeSpan.FromSeconds(Options.Worker.LeaseSeconds), CancellationToken.None);
    }

    /// <summary>Claims the job and runs the engine directly, as the handler would.</summary>
    public async Task<CrawlOutcome> ClaimAndRunAsync(PostgresFixture postgres, Guid jobId)
    {
        Assert.True(await ClaimAsync(jobId));
        return await RunAsync(postgres, jobId);
    }

    /// <summary>Runs the engine for a job this owner has already claimed.</summary>
    public async Task<CrawlOutcome> RunAsync(PostgresFixture postgres, Guid jobId)
    {
        CrawlJob job;
        await using (var db = postgres.CreateDbContext())
        {
            job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId) ?? throw new InvalidOperationException("job missing");
        }

        await using var scope = Provider.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICrawlEngine>();
        return await engine.RunAsync(job, OwnerId, CancellationToken.None);
    }

    public Task<MessageDisposition> HandleAsync(CrawlJobMessage message, int attempt = 1) =>
        HandleAsync(JsonSerializer.SerializeToUtf8Bytes(message, RabbitMqJobMessagePublisher.SerializerOptions), attempt);

    public Task<MessageDisposition> HandleAsync(byte[] body, int attempt = 1)
    {
        var handler = Provider.GetRequiredService<IJobMessageHandler>();
        return handler.HandleAsync(new JobDelivery(body, Guid.NewGuid().ToString(), null, attempt), CancellationToken.None);
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

/// <summary>Skips backoff waits so retry paths run instantly.</summary>
public sealed class NoRetryDelay : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
