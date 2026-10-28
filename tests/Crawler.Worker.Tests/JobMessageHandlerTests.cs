using System.Text;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Worker.Messaging;
using Crawler.Worker.Recovery;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Worker.Tests;

/// <summary>Test plan §7 (W1–W6) through the real handler, engine, state store and database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class JobMessageHandlerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task W1_duplicate_delivery_after_completion_is_acked_without_changes()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(message));
        var afterFirst = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var fetchesAfterFirst = services.Fetcher.Calls.Count;

        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(message));
        var afterSecond = await CrawlSnapshot.LoadAsync(postgres, job.Id);

        Assert.Equal(JobStatus.Completed, afterSecond.Job.Status);
        Assert.Equal(afterFirst.Pages.Count, afterSecond.Pages.Count);
        Assert.Equal(afterFirst.Edges.Count, afterSecond.Edges.Count);
        Assert.Equal(
            (afterFirst.Job.DiscoveredPages, afterFirst.Job.ProcessedPages, afterFirst.Job.FailedPages, afterFirst.Job.Attempts),
            (afterSecond.Job.DiscoveredPages, afterSecond.Job.ProcessedPages, afterSecond.Job.FailedPages, afterSecond.Job.Attempts));
        Assert.Equal(fetchesAfterFirst, services.Fetcher.Calls.Count);
        Assert.Empty(services.Publisher.Published);
    }

    [Fact]
    public async Task W2_invalid_json_is_dead_lettered()
    {
        await using var services = WorkerTestServices.Create(postgres);

        Assert.Equal(MessageDisposition.DeadLetter, await services.HandleAsync(Encoding.UTF8.GetBytes("{not json")));
        Assert.Empty(services.Publisher.Published);
    }

    [Fact]
    public async Task W3_unknown_job_is_dead_lettered()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var message = CrawlJobMessage.For(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(MessageDisposition.DeadLetter, await services.HandleAsync(message));
        Assert.Empty(services.Publisher.Published);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task W4_transient_failure_is_republished_to_retry_with_next_attempt_and_acked(int attempt)
    {
        await using var services = WorkerTestServices.Create(postgres);
        services.Fetcher.OnFetch = (_, _) => throw new InvalidOperationException("simulated infrastructure failure");
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        var disposition = await services.HandleAsync(message, attempt);

        Assert.Equal(MessageDisposition.Ack, disposition);
        var retry = Assert.Single(services.Publisher.Published);
        Assert.Equal(message, retry.Message);
        Assert.Equal(attempt + 1, retry.Attempt);
        Assert.Equal(JobQueue.Retry, retry.Queue);
        Assert.Equal(JobStatus.Running, (await CrawlSnapshot.LoadAsync(postgres, job.Id)).Job.Status);
    }

    [Fact]
    public async Task W4_retry_copy_handled_by_the_same_process_resumes_its_own_lease()
    {
        await using var services = WorkerTestServices.Create(postgres);
        services.Fetcher.OnFetch = (url, _) => url.AbsolutePath == "/b.html"
            ? throw new InvalidOperationException("simulated infrastructure failure")
            : Task.CompletedTask;
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(message, attempt: 1));
        services.Fetcher.OnFetch = null;
        var retry = Assert.Single(services.Publisher.Published);
        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(retry.Message, retry.Attempt));

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((5, 5, 1), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task W5_transient_failure_on_final_attempt_fails_job_and_dead_letters()
    {
        await using var services = WorkerTestServices.Create(postgres);
        services.Fetcher.OnFetch = (_, _) => throw new InvalidOperationException("simulated infrastructure failure");
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);
        var maxAttempts = services.Options.Worker.MaxMessageAttempts;

        var disposition = await services.HandleAsync(message, maxAttempts);

        Assert.Equal(MessageDisposition.DeadLetter, disposition);
        Assert.Empty(services.Publisher.Published);
        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Failed, snapshot.Job.Status);
        Assert.Contains($"after {maxAttempts} attempts", snapshot.Job.FailureReason);
        Assert.Contains(nameof(InvalidOperationException), snapshot.Job.FailureReason); // exception text itself is kept out of the API (D17)
        Assert.NotNull(snapshot.Job.CompletedAt);
        Assert.Null(snapshot.Job.LeaseOwner);
    }

    [Fact]
    public async Task W5b_job_claimed_max_attempts_times_is_failed_from_the_database_regardless_of_header()
    {
        // Models a job whose processing crashed the worker repeatedly: every claim incremented attempts while
        // the message attempt header (crash redelivery, sweeper republish) stayed at 1.
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var maxAttempts = services.Options.Worker.MaxMessageAttempts;
        await using (var db = postgres.CreateDbContext())
        {
            var seeded = await db.Jobs.FindAsync(job.Id);
            seeded!.Status = JobStatus.Running;
            seeded.LeaseOwner = "crashed-worker";
            seeded.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            seeded.Attempts = maxAttempts;
            await db.SaveChangesAsync();
        }
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        var disposition = await services.HandleAsync(message, attempt: 1);

        Assert.Equal(MessageDisposition.DeadLetter, disposition);
        Assert.Empty(services.Publisher.Published);
        Assert.Empty(services.Fetcher.Calls);
        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Failed, snapshot.Job.Status);
        Assert.Contains($"limit {maxAttempts}", snapshot.Job.FailureReason);
        Assert.Null(snapshot.Job.LeaseOwner);

        // A job below the cap with an expired lease is still recoverable.
        var healthy = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 0, maxPages: 10);
        await using (var db = postgres.CreateDbContext())
        {
            var seeded = await db.Jobs.FindAsync(healthy.Id);
            seeded!.Status = JobStatus.Running;
            seeded.LeaseOwner = "crashed-worker";
            seeded.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            seeded.Attempts = maxAttempts - 1;
            await db.SaveChangesAsync();
        }

        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(CrawlJobMessage.For(healthy.Id, healthy.CorrelationId, DateTimeOffset.UtcNow), attempt: 1));
        Assert.Equal(JobStatus.Completed, (await CrawlSnapshot.LoadAsync(postgres, healthy.Id)).Job.Status);
    }

    [Fact]
    public async Task W6_crashed_worker_is_recovered_by_the_sweeper_and_resumed_to_identical_results()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var crashed = WorkerTestServices.Create(postgres, clock: clock);
        await using var survivor = WorkerTestServices.Create(postgres, clock: clock);
        Assert.NotEqual(crashed.OwnerId, survivor.OwnerId);

        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var message = CrawlJobMessage.For(job.Id, job.CorrelationId, DateTimeOffset.UtcNow);

        // First worker persists / and /a.html, then dies while handling /b.html. Its retry copy is
        // discarded: a dead process never publishes it, so only the lease sweeper can recover the job.
        crashed.Fetcher.OnFetch = (url, _) => url.AbsolutePath == "/b.html"
            ? throw new InvalidOperationException("process killed")
            : Task.CompletedTask;
        await crashed.HandleAsync(message);
        var partial = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Running, partial.Job.Status);
        Assert.Equal(crashed.OwnerId, partial.Job.LeaseOwner);
        Assert.Equal(2, partial.Job.ProcessedPages);

        // While the lease is valid the sweeper leaves the job alone and a duplicate is only acked.
        var sweeper = survivor.Provider.GetRequiredService<LeaseRecoveryService>();
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(survivor.Publisher.Published, p => p.Message.JobId == job.Id);
        Assert.Equal(MessageDisposition.Ack, await survivor.HandleAsync(message));
        Assert.Empty(survivor.Fetcher.Calls);

        clock.Advance(TimeSpan.FromSeconds(survivor.Options.Worker.LeaseSeconds + 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        var republished = Assert.Single(survivor.Publisher.Published, p => p.Message.JobId == job.Id);
        Assert.Equal((1, JobQueue.Main), (republished.Attempt, republished.Queue));
        Assert.Equal(job.CorrelationId, republished.Message.CorrelationId);

        Assert.Equal(MessageDisposition.Ack, await survivor.HandleAsync(republished.Message, republished.Attempt));

        Assert.Equal(
            [CrawlSnapshot.Url("/b.html"), CrawlSnapshot.Url("/c.html"), CrawlSnapshot.Url("/d.html")],
            survivor.Fetcher.Calls.Select(u => u.AbsoluteUri).Order(StringComparer.Ordinal).ToArray());

        var recovered = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Completed, recovered.Job.Status);
        Assert.Equal(2, recovered.Job.Attempts);

        // Reference: the same job crawled without interruption.
        await using var reference = WorkerTestServices.Create(postgres);
        var referenceJob = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        await reference.ClaimAndRunAsync(postgres, referenceJob.Id);
        var expected = await CrawlSnapshot.LoadAsync(postgres, referenceJob.Id);

        Assert.Equal(
            (expected.Job.DiscoveredPages, expected.Job.ProcessedPages, expected.Job.FailedPages, expected.Job.Truncated),
            (recovered.Job.DiscoveredPages, recovered.Job.ProcessedPages, recovered.Job.FailedPages, recovered.Job.Truncated));
        Assert.Equal(PageShape(expected), PageShape(recovered));
        Assert.Equal(EdgeShape(expected), EdgeShape(recovered));
        Assert.Equal(recovered.Pages.Count, recovered.Pages.Select(p => p.NormalizedUrl).Distinct().Count());
    }

    private static List<string> PageShape(CrawlSnapshot snapshot)
    {
        var urls = snapshot.Pages.ToDictionary(p => p.Id, p => p.NormalizedUrl);
        return snapshot.Pages
            .Select(p => $"{p.NormalizedUrl}|{p.Depth}|{p.DiscoveryOrder}|{(p.ParentPageId is { } parent ? urls[parent] : "-")}|{p.FetchStatus}|{p.HttpStatusCode}|{p.DomainLinkRatio}")
            .ToList();
    }

    private static List<string> EdgeShape(CrawlSnapshot snapshot)
    {
        var urls = snapshot.Pages.ToDictionary(p => p.Id, p => p.NormalizedUrl);
        return snapshot.Edges
            .Select(e => $"{urls[e.SourcePageId]}|{e.TargetNormalizedUrl}|{e.OccurrenceCount}|{e.IsInternal}|{(e.TargetPageId is { } target ? urls[target] : "-")}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
