using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;
using Crawler.Worker.Crawling;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Worker.Tests;

/// <summary>
/// Test plan §4 (B1–B13). Fixture graph served from tests/fixtures/site under https://example.com/:
/// / → a.html, /b.html, a.html#top (dup), https://external.example.org/x; a → c, / ; b → ./c, d (404); c → e.png.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrawlEngineTests(PostgresFixture postgres)
{
    private const string External = "https://external.example.org/x";

    [Fact]
    public async Task B1_B9_depth_two_crawls_five_pages_with_one_failure_and_completes()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        var outcome = await services.ClaimAndRunAsync(postgres, job.Id);

        Assert.Equal(CrawlOutcome.Completed, outcome);
        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(
            [("/", 0), ("/a.html", 1), ("/b.html", 1), ("/c.html", 2), ("/d.html", 2)],
            snapshot.Pages.Select(p => (new Uri(p.NormalizedUrl).AbsolutePath, p.Depth)).ToArray());
        Assert.DoesNotContain(snapshot.Pages, p => p.NormalizedUrl.EndsWith("e.png", StringComparison.Ordinal));

        var d = snapshot.Page("/d.html");
        Assert.Equal(PageFetchStatus.Failed, d.FetchStatus);
        Assert.Equal(404, d.HttpStatusCode);
        Assert.All(snapshot.Pages.Where(p => p.Id != d.Id), p => Assert.Equal(PageFetchStatus.Fetched, p.FetchStatus));

        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal(5, snapshot.Job.DiscoveredPages);
        Assert.Equal(5, snapshot.Job.ProcessedPages);
        Assert.Equal(1, snapshot.Job.FailedPages);
        Assert.False(snapshot.Job.Truncated);
        Assert.NotNull(snapshot.Job.CompletedAt);
        Assert.Null(snapshot.Job.LeaseOwner);

        // 3 distinct targets from /, 2 from a, 2 from b, 1 from c, none from the failed d.
        Assert.Equal(8, snapshot.Edges.Count);
        Assert.Equal(3, snapshot.EdgesFrom("/").Count);
        Assert.Equal(2, snapshot.EdgesFrom("/a.html").Count);
        Assert.Equal(2, snapshot.EdgesFrom("/b.html").Count);
        Assert.Empty(snapshot.EdgesFrom("/d.html"));
        var toImage = Assert.Single(snapshot.EdgesFrom("/c.html"));
        Assert.Equal(CrawlSnapshot.Url("/e.png"), toImage.TargetNormalizedUrl);
        Assert.True(toImage.IsInternal);
        Assert.Null(toImage.TargetPageId);
        Assert.Equal(snapshot.Page("/d.html").Id, snapshot.Edge("/b.html", "/d.html").TargetPageId);

        Assert.Equal(1.0, snapshot.Page("/a.html").DomainLinkRatio);
        Assert.Equal(1.0, snapshot.Page("/c.html").DomainLinkRatio);
        Assert.Equal(0.0, d.DomainLinkRatio);
        Assert.Equal(5, services.Fetcher.Calls.Count);
    }

    [Fact]
    public async Task B2_root_has_depth_zero_no_parent_and_discovery_order_zero()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var root = (await CrawlSnapshot.LoadAsync(postgres, job.Id)).Page("/");
        Assert.Equal(0, root.Depth);
        Assert.Null(root.ParentPageId);
        Assert.Equal(0, root.DiscoveryOrder);
    }

    [Fact]
    public async Task B3_depth_zero_crawls_only_the_root_but_keeps_its_edges()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 0, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var root = Assert.Single(snapshot.Pages);
        Assert.Equal(WorkerTestServices.StartUrl, root.NormalizedUrl);
        Assert.Equal(3, snapshot.Edges.Count);
        Assert.Equal(2, snapshot.Edge("/", "/a.html").OccurrenceCount);
        Assert.Equal(1, snapshot.Edge("/", "/b.html").OccurrenceCount);
        Assert.Equal(1, snapshot.Edge("/", External).OccurrenceCount);
        Assert.All(snapshot.Edges, e => Assert.Null(e.TargetPageId));
        Assert.False(snapshot.Job.Truncated);
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((1, 1, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task B4_page_limit_truncates_when_an_in_scope_page_gets_no_slot()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 1, maxPages: 2);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal([WorkerTestServices.StartUrl, CrawlSnapshot.Url("/a.html")], snapshot.Pages.Select(p => p.NormalizedUrl).ToArray());
        Assert.True(snapshot.Job.Truncated);
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((2, 2, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
        Assert.Null(snapshot.Edge("/", "/b.html").TargetPageId);
    }

    [Fact]
    public async Task B5_cycle_back_to_root_is_an_edge_and_root_is_not_reprocessed()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var cycle = snapshot.Edge("/a.html", "/");
        Assert.Equal(snapshot.Page("/").Id, cycle.TargetPageId);
        Assert.True(cycle.IsInternal);
        Assert.Single(services.Fetcher.Calls, u => u.AbsoluteUri == WorkerTestServices.StartUrl);
    }

    [Fact]
    public async Task B6_page_discovered_twice_has_one_row_with_first_parent_in_level_order()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var c = Assert.Single(snapshot.Pages, p => p.NormalizedUrl == CrawlSnapshot.Url("/c.html"));
        Assert.Equal(snapshot.Page("/a.html").Id, c.ParentPageId);
        Assert.Equal(c.Id, snapshot.Edge("/b.html", "/c.html").TargetPageId);
    }

    [Fact]
    public async Task B7_external_link_is_never_fetched_and_stored_as_external_edge()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var external = snapshot.Edge("/", External);
        Assert.False(external.IsInternal);
        Assert.Null(external.TargetPageId);
        Assert.DoesNotContain(services.Fetcher.Calls, u => u.Host == "external.example.org");
    }

    [Fact]
    public async Task B8_parent_assignment_is_stable_across_runs()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var first = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        var second = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, first.Id);
        await services.ClaimAndRunAsync(postgres, second.Id);

        Assert.Equal(await TreeShapeAsync(first.Id), await TreeShapeAsync(second.Id));
        Assert.Equal(5, (await TreeShapeAsync(first.Id)).Count);
    }

    [Fact]
    public async Task B10_non_html_page_at_max_depth_is_skipped_without_links()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 3, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        var image = snapshot.Page("/e.png");
        Assert.Equal(3, image.Depth);
        Assert.Equal(PageFetchStatus.Skipped, image.FetchStatus);
        Assert.Contains("image/png", image.ErrorMessage);
        Assert.Equal(0, image.OutgoingLinkCount);
        Assert.Equal(0.0, image.DomainLinkRatio);
        Assert.Empty(snapshot.EdgesFrom("/e.png"));
        Assert.Equal((6, 6, 1), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task B11_root_ratio_counts_every_occurrence()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);

        await services.ClaimAndRunAsync(postgres, job.Id);

        var root = (await CrawlSnapshot.LoadAsync(postgres, job.Id)).Page("/");
        Assert.Equal(4, root.OutgoingLinkCount);
        Assert.Equal(3, root.InternalLinkCount);
        Assert.Equal(0.75, root.DomainLinkRatio);
        Assert.Equal(200, root.HttpStatusCode);
    }

    [Fact]
    public async Task B12_cancellation_is_observed_between_pages()
    {
        // Concurrency 1 makes "no further fetches" exact: no look-ahead fetch is in flight at the boundary.
        await using var services = WorkerTestServices.Create(postgres, new Dictionary<string, string?> { ["Crawler:Http:Concurrency"] = "1" });
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        services.Fetcher.OnFetch = async (_, call) =>
        {
            if (call == 2)
            {
                await using var scope = services.Provider.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<IJobStateStore>().RequestCancellationAsync(job.Id, CancellationToken.None);
                Assert.Equal(CancellationRequestOutcome.Requested, outcome);
            }
        };

        var result = await services.ClaimAndRunAsync(postgres, job.Id);

        Assert.Equal(CrawlOutcome.Canceled, result);
        Assert.Equal(2, services.Fetcher.Calls.Count);
        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Canceled, snapshot.Job.Status);
        Assert.Equal(PageFetchStatus.Fetched, snapshot.Page("/").FetchStatus);
        Assert.Equal(PageFetchStatus.Fetched, snapshot.Page("/a.html").FetchStatus);
        Assert.All([snapshot.Page("/b.html"), snapshot.Page("/c.html")], p =>
        {
            Assert.Equal(PageFetchStatus.Discovered, p.FetchStatus);
            Assert.Null(p.FetchedAt);
        });
        Assert.Equal((4, 2, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task B13_resume_processes_only_the_durable_frontier()
    {
        await using var services = WorkerTestServices.Create(postgres);
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 2, maxPages: 200);
        Assert.True(await services.ClaimAsync(job.Id));
        await SeedProcessedRootAsync(job);

        await services.RunAsync(postgres, job.Id);

        Assert.DoesNotContain(services.Fetcher.Calls, u => u.AbsoluteUri == WorkerTestServices.StartUrl);
        Assert.Equal(
            [CrawlSnapshot.Url("/a.html"), CrawlSnapshot.Url("/b.html"), CrawlSnapshot.Url("/c.html"), CrawlSnapshot.Url("/d.html")],
            services.Fetcher.Calls.Select(u => u.AbsoluteUri).Order(StringComparer.Ordinal).ToArray());

        var snapshot = await CrawlSnapshot.LoadAsync(postgres, job.Id);
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal(5, snapshot.Pages.Count);
        Assert.Equal((5, 5, 1), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
        Assert.Equal(snapshot.Pages.Count, snapshot.Job.DiscoveredPages);
        Assert.Equal(snapshot.Pages.Count(p => p.FetchStatus != PageFetchStatus.Discovered), snapshot.Job.ProcessedPages);
        Assert.Equal(8, snapshot.Edges.Count);
        Assert.Equal(snapshot.Page("/a.html").Id, snapshot.Page("/c.html").ParentPageId);
        Assert.Equal(3, snapshot.Page("/c.html").DiscoveryOrder);
        Assert.Equal(4, snapshot.Page("/d.html").DiscoveryOrder);
    }

    /// <summary>State after the root was processed by an earlier (crashed) run: root Fetched with its edges, a and b Discovered.</summary>
    private async Task SeedProcessedRootAsync(CrawlJob job)
    {
        await using var db = postgres.CreateDbContext();
        var startUrl = new Uri(WorkerTestServices.StartUrl);
        var root = CrawlPage.CreateRoot(job.Id, startUrl);
        var links = new[] { "a.html", "b.html", "a.html", External }.Select(l => new Uri(startUrl, l)).ToList();
        root.MarkFetched(200, DomainLinkRatio.Calculate(links, job.StartingHost), DateTimeOffset.UtcNow);
        var a = CrawlPage.CreateChild(job.Id, new Uri(startUrl, "a.html"), 1, root.Id, 1);
        var b = CrawlPage.CreateChild(job.Id, new Uri(startUrl, "b.html"), 1, root.Id, 2);
        db.Pages.AddRange(root, a, b);
        db.Edges.AddRange(
            new CrawlEdge { JobId = job.Id, SourcePageId = root.Id, TargetNormalizedUrl = a.NormalizedUrl, TargetPageId = a.Id, OccurrenceCount = 2, IsInternal = true },
            new CrawlEdge { JobId = job.Id, SourcePageId = root.Id, TargetNormalizedUrl = b.NormalizedUrl, TargetPageId = b.Id, OccurrenceCount = 1, IsInternal = true },
            new CrawlEdge { JobId = job.Id, SourcePageId = root.Id, TargetNormalizedUrl = External, OccurrenceCount = 1, IsInternal = false });
        var tracked = await db.Jobs.FindAsync(job.Id);
        tracked!.DiscoveredPages = 3;
        tracked.ProcessedPages = 1;
        await db.SaveChangesAsync();
    }

    private async Task<List<(string Url, int Depth, string? ParentUrl, int DiscoveryOrder)>> TreeShapeAsync(Guid jobId)
    {
        var snapshot = await CrawlSnapshot.LoadAsync(postgres, jobId);
        var urls = snapshot.Pages.ToDictionary(p => p.Id, p => p.NormalizedUrl);
        return snapshot.Pages
            .Select(p => (p.NormalizedUrl, p.Depth, p.ParentPageId is { } parent ? urls[parent] : null, p.DiscoveryOrder))
            .OrderBy(t => t.DiscoveryOrder)
            .ToList();
    }
}
