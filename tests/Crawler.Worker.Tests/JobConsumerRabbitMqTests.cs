using System.Text;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Crawler.Worker.Tests;

/// <summary>
/// Real broker round trip (W2/W3 at the AMQP level, plus a full consume → crawl → ack) with the worker's
/// hosted services running; only the page fetcher is replaced by the fixture-site fake.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class JobConsumerRabbitMqTests(PostgresFixture postgres, RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private IHost _worker = null!;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["RabbitMq:Host"] = rabbit.Host,
            ["RabbitMq:Port"] = rabbit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RabbitMq:UserName"] = rabbit.UserName,
            ["RabbitMq:Password"] = rabbit.Password,
            ["Crawler:Worker:RetryDelaySeconds"] = "1",
        });
        builder.Services.AddCrawlerWorker(builder.Configuration);
        builder.Services.RemoveAll<IPageFetcher>();
        builder.Services.AddSingleton<IPageFetcher, FixtureSiteFetcher>();

        _worker = builder.Build();
        await _worker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync();
        _worker.Dispose();
    }

    [Fact]
    public async Task Garbage_message_is_nacked_into_the_dead_letter_queue()
    {
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, TimeSpan.FromSeconds(1), CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitMqTopology.DeadLetterQueue);
        var body = Encoding.UTF8.GetBytes($"garbage-{Guid.NewGuid():N}");

        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, RabbitMqTopology.JobsQueue, mandatory: true,
            new BasicProperties { Persistent = true, MessageId = Guid.NewGuid().ToString() }, body);

        var deadLettered = await WaitForAsync(async () => await channel.BasicGetAsync(RabbitMqTopology.DeadLetterQueue, autoAck: true));
        Assert.Equal(body, deadLettered.Body.ToArray());
    }

    [Fact]
    public async Task Unknown_job_is_nacked_into_the_dead_letter_queue()
    {
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, TimeSpan.FromSeconds(1), CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitMqTopology.DeadLetterQueue);
        var message = CrawlJobMessage.For(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        await _worker.Services.GetRequiredService<IJobMessagePublisher>().PublishAsync(message, 1, JobQueue.Main, CancellationToken.None);

        var deadLettered = await WaitForAsync(async () => await channel.BasicGetAsync(RabbitMqTopology.DeadLetterQueue, autoAck: true));
        Assert.Equal(message.MessageId.ToString(), deadLettered.BasicProperties.MessageId);
    }

    [Fact]
    public async Task Valid_message_for_seeded_job_is_consumed_and_the_job_completes()
    {
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        await _worker.Services.GetRequiredService<IJobMessagePublisher>().PublishAsync(message, 1, JobQueue.Main, CancellationToken.None);

        var snapshot = await WaitForAsync(async () =>
        {
            var current = await CrawlSnapshot.LoadAsync(postgres, job.Id);
            return current.Job.Status == JobStatus.Completed ? current : null;
        });
        Assert.Equal((5, 5, 1), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
        Assert.Equal(5, snapshot.Pages.Count);
        Assert.Equal(8, snapshot.Edges.Count);

        // Acked: nothing is left in the work queue for this job.
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.Equal(0u, await channel.MessageCountAsync(RabbitMqTopology.JobsQueue));
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"Condition not met within {WaitLimit}");
    }
}

/// <summary>Real RabbitMQ: <c>CRAWLER_TEST_RABBITMQ</c> (amqp://user:pass@host:port/) when set, otherwise a Testcontainer.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public string UserName { get; private set; } = string.Empty;

    public string Password { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("CRAWLER_TEST_RABBITMQ");
        Uri uri;
        if (!string.IsNullOrWhiteSpace(external))
        {
            uri = new Uri(external);
        }
        else
        {
            _container = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
                .WithUsername("crawler")
                .WithPassword("crawler")
                .Build();
            await _container.StartAsync();
            uri = new Uri(_container.GetConnectionString());
        }

        Host = uri.Host;
        Port = uri.Port;
        var credentials = uri.UserInfo.Split(':', 2);
        UserName = Uri.UnescapeDataString(credentials[0]);
        Password = Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? string.Empty);
    }

    public Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { HostName = Host, Port = Port, UserName = UserName, Password = Password }.CreateConnectionAsync();

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
