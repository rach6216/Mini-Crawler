using System.Text;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Crawling;
using Crawler.Worker.Crawling;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Worker.Tests;

/// <summary>
/// contracts §7 robots.txt: crawls of tests/fixtures/site/robots/ on a real Kestrel server through the
/// production HttpPageFetcher, RobotsPolicyProvider and CrawlEngine. /robots.txt disallows /robots/blocked.html.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RobotsTxtCrawlTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Root = "/robots/";
    private const string Allowed = "/robots/allowed.html";
    private const string Blocked = "/robots/blocked.html";

    private TestSiteServer _server = null!;

    public async Task InitializeAsync() => _server = await TestSiteServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Disallowed_page_is_skipped_without_being_requested_and_job_completes()
    {
        await using var services = WorkerTestServices.CreateWithHttp(postgres);

        var (outcome, snapshot) = await CrawlAsync(services);

        Assert.Equal(CrawlOutcome.Completed, outcome);
        Assert.Equal([Root, Allowed, Blocked], snapshot.Pages.Select(p => new Uri(p.NormalizedUrl).AbsolutePath).ToArray());

        var blocked = Page(snapshot, Blocked);
        Assert.Equal(PageFetchStatus.Skipped, blocked.FetchStatus);
        Assert.Equal(RobotsDecision.DisallowedReason, blocked.ErrorMessage);
        Assert.Null(blocked.HttpStatusCode);
        Assert.Equal(0, blocked.OutgoingLinkCount);
        Assert.Equal(0.0, blocked.DomainLinkRatio);
        Assert.DoesNotContain(snapshot.Edges, e => e.SourcePageId == blocked.Id);
        Assert.Equal(PageFetchStatus.Fetched, Page(snapshot, Root).FetchStatus);
        Assert.Equal(PageFetchStatus.Fetched, Page(snapshot, Allowed).FetchStatus);

        Assert.Equal(0, _server.Hits(Blocked));
        Assert.Equal(1, _server.Hits(Root));
        Assert.Equal(1, _server.Hits(Allowed));
        Assert.Equal(1, _server.Hits("/robots.txt"));

        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((3, 3, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task Server_error_on_robots_txt_disallows_every_page_including_the_root()
    {
        _server.RobotsOverride = () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var services = WorkerTestServices.CreateWithHttp(postgres);

        var (outcome, snapshot) = await CrawlAsync(services);

        Assert.Equal(CrawlOutcome.Completed, outcome);
        var root = Assert.Single(snapshot.Pages);
        Assert.Equal(PageFetchStatus.Skipped, root.FetchStatus);
        Assert.Equal("robots.txt unavailable: HTTP 503", root.ErrorMessage);
        Assert.Empty(snapshot.Edges);
        Assert.Equal(0, _server.Hits(Root));
        Assert.Equal(services.Options.Http.MaxAttempts, _server.Hits("/robots.txt"));
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((1, 1, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    [Fact]
    public async Task Missing_robots_txt_allows_everything()
    {
        _server.RobotsOverride = () => Results.NotFound();
        await using var services = WorkerTestServices.CreateWithHttp(postgres);

        var (outcome, snapshot) = await CrawlAsync(services);

        AssertAllFetched(outcome, snapshot);
        Assert.Equal(1, _server.Hits("/robots.txt"));
    }

    [Fact]
    public async Task Redirect_of_robots_txt_to_another_host_allows_everything()
    {
        _server.RobotsOverride = () => Results.Redirect($"http://localhost:{_server.Port}/elsewhere/robots.txt");
        await using var services = WorkerTestServices.CreateWithHttp(postgres);

        var (outcome, snapshot) = await CrawlAsync(services);

        AssertAllFetched(outcome, snapshot);
        Assert.Equal(0, _server.Hits("/elsewhere/robots.txt"));
    }

    [Fact]
    public async Task Disabled_RespectRobotsTxt_fetches_the_disallowed_page_and_never_requests_robots_txt()
    {
        await using var services = WorkerTestServices.CreateWithHttp(postgres, new Dictionary<string, string?>
        {
            ["Crawler:Http:RespectRobotsTxt"] = "false",
        });

        var (outcome, snapshot) = await CrawlAsync(services);

        AssertAllFetched(outcome, snapshot);
        Assert.Equal(0, _server.Hits("/robots.txt"));
    }

    [Fact]
    public async Task Robots_txt_beyond_500_KiB_is_ignored()
    {
        var padding = new StringBuilder("User-agent: *\n");
        while (padding.Length < RobotsPolicyProvider.MaxRobotsBytes)
        {
            padding.Append("# padding line to push the next rule past the RFC 9309 parsing limit\n");
        }

        _server.RobotsOverride = () => Results.Text(padding + "Disallow: /robots/\n", "text/plain");
        await using var services = WorkerTestServices.CreateWithHttp(postgres);
        var provider = services.Provider.GetRequiredService<IRobotsPolicyProvider>();

        var decision = await provider.GetAsync(_server.Url(Root), CancellationToken.None);

        Assert.Null(decision.UnavailableReason);
        Assert.Equal(0, decision.Rules.RuleCount);
        Assert.True(decision.IsAllowed(_server.Url(Blocked)));
    }

    private async Task<(CrawlOutcome Outcome, CrawlSnapshot Snapshot)> CrawlAsync(WorkerTestServices services)
    {
        var job = await WorkerTestServices.SeedJobAsync(postgres, maxDepth: 1, maxPages: 200, startUrl: _server.Url(Root).AbsoluteUri);
        var outcome = await services.ClaimAndRunAsync(postgres, job.Id);
        return (outcome, await CrawlSnapshot.LoadAsync(postgres, job.Id));
    }

    private void AssertAllFetched(CrawlOutcome outcome, CrawlSnapshot snapshot)
    {
        Assert.Equal(CrawlOutcome.Completed, outcome);
        Assert.Equal([Root, Allowed, Blocked], snapshot.Pages.Select(p => new Uri(p.NormalizedUrl).AbsolutePath).ToArray());
        Assert.All(snapshot.Pages, p => Assert.Equal(PageFetchStatus.Fetched, p.FetchStatus));
        Assert.Equal(1, _server.Hits(Blocked));
        Assert.Equal(JobStatus.Completed, snapshot.Job.Status);
        Assert.Equal((3, 3, 0), (snapshot.Job.DiscoveredPages, snapshot.Job.ProcessedPages, snapshot.Job.FailedPages));
    }

    private CrawlPage Page(CrawlSnapshot snapshot, string path) =>
        snapshot.Pages.Single(p => p.NormalizedUrl == _server.Url(path).AbsoluteUri);
}
