using Crawler.Api.Tests.Infrastructure;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Api.Tests;

/// <summary>Test plan section 5, S3–S12: conditional state transitions against real PostgreSQL.</summary>
[Collection(InfrastructureCollection.Name)]
public sealed class JobStateStoreTests(InfrastructureFixture infrastructure)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly CrawlerApiFactory _factory = infrastructure.Factory("state_store_tests");

    [Fact]
    public async Task S3_TryClaim_on_pending_job_starts_it_with_a_lease()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Pending));
        var before = DateTimeOffset.UtcNow;

        var claimed = await WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-a", Lease, default));

        Assert.True(claimed);
        var row = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Running, row.Status);
        Assert.NotNull(row.StartedAt);
        Assert.Equal("worker-a", row.LeaseOwner);
        Assert.NotNull(row.LeaseExpiresAt);
        Assert.True(row.LeaseExpiresAt > before.Add(Lease).AddSeconds(-5));
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task S4_TryClaim_on_running_job_with_live_lease_of_another_owner_is_rejected()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Running, leaseOwner: "worker-b", leaseExpiresAt: TestData.UtcNow().AddMinutes(5)));
        var before = await LoadAsync(job.Id);

        var claimed = await WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-a", Lease, default));

        Assert.False(claimed);
        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Running, after.Status);
        Assert.Equal("worker-b", after.LeaseOwner);
        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.Attempts, after.Attempts);
    }

    [Fact]
    public async Task S5_TryClaim_on_running_job_with_expired_lease_reclaims_it()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Running, leaseOwner: "worker-b", leaseExpiresAt: TestData.UtcNow().AddMinutes(-1)));
        var before = await LoadAsync(job.Id);

        var claimed = await WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-a", Lease, default));

        Assert.True(claimed);
        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Running, after.Status);
        Assert.Equal("worker-a", after.LeaseOwner);
        Assert.Equal(2, after.Attempts);
        Assert.Equal(before.StartedAt, after.StartedAt);
    }

    [Fact]
    public async Task S6_TryClaim_on_completed_job_is_rejected()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Completed));

        var claimed = await WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-a", Lease, default));

        Assert.False(claimed);
        Assert.Equal(JobStatus.Completed, (await LoadAsync(job.Id)).Status);
    }

    [Fact]
    public async Task S7_TryComplete_with_wrong_owner_is_rejected()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Running, leaseOwner: "worker-a"));

        var completed = await WithStoreAsync(store => store.TryCompleteAsync(job.Id, "worker-b", default));

        Assert.False(completed);
        var row = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Running, row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task S8_RequestCancellation_on_pending_job_cancels_it_immediately()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Pending));

        var outcome = await WithStoreAsync(store => store.RequestCancellationAsync(job.Id, default));

        Assert.Equal(CancellationRequestOutcome.Canceled, outcome);
        var row = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Canceled, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.True(row.CancellationRequested);
        Assert.NotNull(row.CancellationRequestedAt);
    }

    [Fact]
    public async Task S9_RequestCancellation_on_running_job_sets_flag_and_is_idempotent()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Running));

        var first = await WithStoreAsync(store => store.RequestCancellationAsync(job.Id, default));
        var afterFirst = await LoadAsync(job.Id);
        var second = await WithStoreAsync(store => store.RequestCancellationAsync(job.Id, default));
        var afterSecond = await LoadAsync(job.Id);

        Assert.Equal(CancellationRequestOutcome.Requested, first);
        Assert.Equal(JobStatus.Running, afterFirst.Status);
        Assert.True(afterFirst.CancellationRequested);
        Assert.Null(afterFirst.CompletedAt);

        Assert.Equal(CancellationRequestOutcome.AlreadyRequested, second);
        Assert.Equal(JobStatus.Running, afterSecond.Status);
        Assert.Equal(afterFirst.CancellationRequestedAt, afterSecond.CancellationRequestedAt);
    }

    [Fact]
    public async Task S10_RequestCancellation_on_completed_job_is_not_applied()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Completed));

        var outcome = await WithStoreAsync(store => store.RequestCancellationAsync(job.Id, default));

        Assert.Equal(CancellationRequestOutcome.AlreadyTerminal, outcome);
        var row = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Completed, row.Status);
        Assert.False(row.CancellationRequested);
    }

    [Fact]
    public async Task S11_TryComplete_succeeds_after_cancellation_flag_is_set()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Running, leaseOwner: "worker-a"));
        await WithStoreAsync(store => store.RequestCancellationAsync(job.Id, default));

        var completed = await WithStoreAsync(store => store.TryCompleteAsync(job.Id, "worker-a", default));

        Assert.True(completed);
        var row = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task S12_concurrent_claims_for_the_same_pending_job_have_exactly_one_winner()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Pending));

        // Separate scopes give each claim its own DbContext and connection, as two workers would have.
        var claims = await Task.WhenAll(
            Task.Run(() => WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-a", Lease, default))),
            Task.Run(() => WithStoreAsync(store => store.TryClaimAsync(job.Id, "worker-b", Lease, default))));

        Assert.Single(claims, c => c);
        var row = await LoadAsync(job.Id);
        Assert.Equal(1, row.Attempts);
        Assert.Contains(row.LeaseOwner, new[] { "worker-a", "worker-b" });
    }

    [Fact]
    public async Task FindStalePending_returns_old_uncanceled_pending_jobs_oldest_first()
    {
        var now = TestData.UtcNow();
        var newer = await SeedAsync(TestData.Job(JobStatus.Pending, createdAt: now.AddHours(-2)));
        var older = await SeedAsync(TestData.Job(JobStatus.Pending, createdAt: now.AddHours(-3)));
        var fresh = await SeedAsync(TestData.Job(JobStatus.Pending, createdAt: now.AddMinutes(-1)));
        var running = await SeedAsync(TestData.Job(JobStatus.Running, createdAt: now.AddHours(-3), leaseExpiresAt: now.AddHours(-2)));
        var cancelRequested = TestData.Job(JobStatus.Pending, createdAt: now.AddHours(-3));
        cancelRequested.CancellationRequested = true;
        await SeedAsync(cancelRequested);
        var ours = new HashSet<Guid> { newer.Id, older.Id, fresh.Id, running.Id, cancelRequested.Id };

        var stale = await WithStoreAsync(store => store.FindStalePendingAsync(TimeSpan.FromHours(1), 1000, default));

        Assert.Equal(
            [new StalePendingJob(older.Id, older.CorrelationId, older.CreatedAt), new StalePendingJob(newer.Id, newer.CorrelationId, newer.CreatedAt)],
            stale.Where(s => ours.Contains(s.JobId)));
    }

    private async Task<CrawlJob> SeedAsync(CrawlJob job)
    {
        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        });
        return job;
    }

    private Task<CrawlJob> LoadAsync(Guid jobId) =>
        _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId));

    private async Task<T> WithStoreAsync<T>(Func<IJobStateStore, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IJobStateStore>());
    }
}
