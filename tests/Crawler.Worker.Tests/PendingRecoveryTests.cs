using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Worker.Messaging;
using Crawler.Worker.Recovery;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Worker.Tests;

/// <summary>
/// A job whose message was lost stays Pending forever unless the sweeper republishes it. The database is shared
/// with other tests, so every assertion is filtered by the job id this test created.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PendingRecoveryTests(PostgresFixture postgres)
{
    // Large enough that Pending leftovers of other tests in the shared database cannot crowd this test's job
    // out of a sweep batch.
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Crawler:Worker:RecoverySweepBatchSize"] = "1000",
    };

    [Fact]
    public async Task Stale_pending_job_is_republished_once_per_cooldown_window()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var services = WorkerTestServices.Create(postgres, Settings, clock);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var sweeper = services.Provider.GetRequiredService<LeaseRecoveryService>();

        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.PendingRepublishAfterSeconds + 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        var republished = Assert.Single(RepublishedFor(services, job.Id));
        Assert.Equal((1, JobQueue.Main), (republished.Attempt, republished.Queue));
        Assert.Equal(job.CorrelationId, republished.Message.CorrelationId);

        // Still Pending (nobody consumed the copy yet), but inside the cooldown window: no second copy.
        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.RepublishCooldownSeconds - 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Single(RepublishedFor(services, job.Id));

        clock.Advance(TimeSpan.FromSeconds(1));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Equal(2, RepublishedFor(services, job.Id).Count);

        await CancelAsync(services, job.Id);
    }

    [Fact]
    public async Task Fresh_pending_job_is_not_republished()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var services = WorkerTestServices.Create(postgres, Settings, clock);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var sweeper = services.Provider.GetRequiredService<LeaseRecoveryService>();

        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.PendingRepublishAfterSeconds - 5));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(RepublishedFor(services, job.Id));
        await CancelAsync(services, job.Id);
    }

    [Fact]
    public async Task Stale_pending_job_with_cancellation_requested_is_not_republished()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var services = WorkerTestServices.Create(postgres, Settings, clock);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.CancellationRequested, true));
        }

        var sweeper = services.Provider.GetRequiredService<LeaseRecoveryService>();
        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.PendingRepublishAfterSeconds + 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(RepublishedFor(services, job.Id));
        await CancelAsync(services, job.Id);
    }

    [Fact]
    public async Task Republished_stale_pending_job_completes_and_duplicate_copies_stay_harmless()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var services = WorkerTestServices.Create(postgres, Settings, clock);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var sweeper = services.Provider.GetRequiredService<LeaseRecoveryService>();

        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.PendingRepublishAfterSeconds + 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        var republished = Assert.Single(RepublishedFor(services, job.Id));

        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(republished.Message, republished.Attempt));
        var completed = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Completed, completed.Job.Status);
        Assert.Equal(1, completed.Job.Attempts);
        var fetches = services.Fetcher.Calls.Count;

        // The original message turns up after all (it was only delayed): acknowledged without any change.
        var original = CrawlJobMessage.For(job.Id, job.CorrelationId, job.CreatedAt);
        Assert.Equal(MessageDisposition.Ack, await services.HandleAsync(original));
        var afterDuplicate = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Completed, afterDuplicate.Job.Status);
        Assert.Equal(
            (completed.Job.DiscoveredPages, completed.Job.ProcessedPages, completed.Job.Attempts, completed.Pages.Count, completed.Edges.Count),
            (afterDuplicate.Job.DiscoveredPages, afterDuplicate.Job.ProcessedPages, afterDuplicate.Job.Attempts, afterDuplicate.Pages.Count, afterDuplicate.Edges.Count));
        Assert.Equal(fetches, services.Fetcher.Calls.Count);

        // A completed job is no longer Pending, so later sweeps leave it alone.
        clock.Advance(TimeSpan.FromSeconds(services.Options.Worker.RepublishCooldownSeconds + 1));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        Assert.Single(RepublishedFor(services, job.Id));
    }

    private static List<PublishedMessage> RepublishedFor(WorkerTestServices services, Guid jobId) =>
        services.Publisher.Published.Where(p => p.Message.JobId == jobId).ToList();

    /// <summary>Leaves no Pending job behind for other tests' sweepers (the consumer tests run a real one).</summary>
    private static async Task CancelAsync(WorkerTestServices services, Guid jobId)
    {
        await using var scope = services.Provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
        var outcome = await store.RequestCancellationAsync(jobId, CancellationToken.None);
        Assert.True(outcome == CancellationRequestOutcome.Canceled, outcome.ToString());
    }
}
