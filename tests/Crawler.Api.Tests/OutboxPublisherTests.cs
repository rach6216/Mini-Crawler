using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Crawler.Api.Tests.Infrastructure;
using Crawler.Core.Application.Contracts;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Core.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Crawler.Api.Tests;

/// <summary>Test plan A13 plus readiness, against real RabbitMQ.</summary>
[Collection(InfrastructureCollection.Name)]
public sealed class OutboxPublisherTests(InfrastructureFixture infrastructure)
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A13_publishing_round_sends_pending_rows_and_stamps_PublishedAt()
    {
        var factory = infrastructure.Factory("outbox_round_tests");
        var job = await SeedJobWithOutboxAsync(factory);

        var result = await PublisherFor(factory).PublishPendingAsync(CancellationToken.None);

        Assert.True(result.Published >= 1);
        Assert.Equal(0, result.Failed);
        var row = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(o => o.JobId == job.Id));
        Assert.NotNull(row.PublishedAt);
        Assert.Equal(0, row.PublishAttempts);

        var delivered = await ReceiveJobMessageAsync(job.Id);
        Assert.Equal(job.CorrelationId, delivered.CorrelationId);

        var second = await PublisherFor(factory).PublishPendingAsync(CancellationToken.None);
        Assert.Equal(0, second.Selected);
    }

    [Fact]
    public async Task A13_hosted_publisher_publishes_a_created_job_promptly()
    {
        var factory = infrastructure.Factory("outbox_hosted_tests", s => s.OutboxEnabled = true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/hosted" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<CreateJobResponse>(CrawlerApiFactory.Json))!.JobId;

        var deadline = DateTime.UtcNow + PublishTimeout;
        DateTimeOffset? publishedAt = null;
        while (publishedAt is null && DateTime.UtcNow < deadline)
        {
            publishedAt = await factory.WithDbAsync(db =>
                db.OutboxMessages.AsNoTracking().Where(o => o.JobId == jobId).Select(o => o.PublishedAt).SingleAsync());
            if (publishedAt is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(publishedAt);
        var delivered = await ReceiveJobMessageAsync(jobId);
        Assert.Equal(jobId, delivered.JobId);
    }

    [Fact]
    public async Task A13_unreachable_broker_increments_attempts_and_keeps_rows_unpublished()
    {
        var factory = infrastructure.Factory("outbox_broker_down_tests",
            s => s.RabbitMq = s.RabbitMq with { Host = "127.0.0.1", Port = UnusedTcpPort() });
        var job = await SeedJobWithOutboxAsync(factory);
        var publisher = PublisherFor(factory);

        var first = await publisher.PublishPendingAsync(CancellationToken.None);
        var second = await publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(1, first.Failed);
        Assert.Equal(1, second.Failed);
        var row = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(o => o.JobId == job.Id));
        Assert.Null(row.PublishedAt);
        Assert.Equal(2, row.PublishAttempts);
        Assert.False(string.IsNullOrEmpty(row.LastError));
        Assert.True(row.LastError!.Length <= 2000);
    }

    [Fact]
    public async Task Readiness_is_healthy_with_postgres_and_rabbitmq()
    {
        var factory = infrastructure.Factory("outbox_round_tests");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_without_rabbitmq()
    {
        var factory = infrastructure.Factory("outbox_broker_down_tests",
            s => s.RabbitMq = s.RabbitMq with { Host = "127.0.0.1", Port = UnusedTcpPort() });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void Backoff_doubles_per_failure_and_is_capped(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxPublisherService.Backoff(new OutboxOptions { PollIntervalSeconds = 2 }, failures));
    }

    [Fact]
    public async Task Signal_wakes_a_waiter_and_coalesces_notifications()
    {
        var signal = new OutboxSignal();

        signal.Notify();
        signal.Notify();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        signal.Notify();
        Assert.True(await waiting);
    }

    private static OutboxPublisherService PublisherFor(CrawlerApiFactory factory) =>
        // Hosted publishing is disabled in these factories, so the round is driven explicitly.
        ActivatorUtilities.CreateInstance<OutboxPublisherService>(factory.Services);

    private static async Task<CrawlJob> SeedJobWithOutboxAsync(CrawlerApiFactory factory)
    {
        var job = TestData.Job(JobStatus.Pending, url: $"https://example.com/{Guid.NewGuid():N}");
        await factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            db.OutboxMessages.Add(CrawlJobOutbox.CreateRequested(job, job.CreatedAt));
            await db.SaveChangesAsync();
        });
        return job;
    }

    /// <summary>Drains <c>crawl.jobs</c> until the message for <paramref name="jobId"/> arrives.</summary>
    private async Task<CrawlJobMessage> ReceiveJobMessageAsync(Guid jobId)
    {
        var endpoint = infrastructure.RabbitMq;
        var connectionFactory = new ConnectionFactory
        {
            HostName = endpoint.Host,
            Port = endpoint.Port,
            UserName = endpoint.UserName,
            Password = endpoint.Password,
            VirtualHost = endpoint.VirtualHost,
        };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var deadline = DateTime.UtcNow + PublishTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var delivery = await channel.BasicGetAsync(RabbitMqTopology.JobsQueue, autoAck: true);
            if (delivery is null)
            {
                await Task.Delay(100);
                continue;
            }

            var message = JsonSerializer.Deserialize<CrawlJobMessage>(delivery.Body.Span, RabbitMqJobMessagePublisher.SerializerOptions);
            if (message?.JobId == jobId)
            {
                Assert.Equal(message.MessageId.ToString(), delivery.BasicProperties.MessageId);
                Assert.Equal(message.CorrelationId.ToString(), delivery.BasicProperties.CorrelationId);
                Assert.Equal(1, RabbitMqTopology.ReadAttempt(delivery.BasicProperties));
                return message;
            }
        }

        throw new TimeoutException($"No message for job {jobId} arrived on {RabbitMqTopology.JobsQueue}.");
    }

    private static int UnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
