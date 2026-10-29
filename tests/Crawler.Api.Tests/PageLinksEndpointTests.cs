using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Tests.Infrastructure;
using Crawler.Core.Application.Contracts;
using Crawler.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Crawler.Api.Tests;

/// <summary>GET /api/jobs/{jobId}/pages/{pageId}/links: the per-page list of discovered outgoing links.</summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PageLinksEndpointTests(InfrastructureFixture infrastructure)
{
    private const string ExternalUrl = "https://external.example.org/x";

    private readonly CrawlerApiFactory _factory = infrastructure.Factory("page_links_tests");

    [Fact]
    public async Task Links_are_returned_in_insertion_order_with_target_status()
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(LinksPath(seeded.Job.Id, seeded.Root.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<PageLinkDto>>(CrawlerApiFactory.Json);
        Assert.NotNull(result);
        Assert.Equal((1, 100, 3), (result.Page, result.PageSize, result.TotalCount));
        Assert.Equal(
            [
                new PageLinkDto(seeded.Crawled.NormalizedUrl, 2, true, seeded.Crawled.Id, PageFetchStatus.Fetched),
                new PageLinkDto(seeded.Uncrawled.NormalizedUrl, 1, true, seeded.Uncrawled.Id, PageFetchStatus.Discovered),
                new PageLinkDto(ExternalUrl, 1, false, null, null),
            ],
            result.Items);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"targetFetchStatus\":\"Fetched\"", raw);
        Assert.Contains("\"targetFetchStatus\":null", raw);
    }

    [Fact]
    public async Task Links_are_paged()
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var first = await client.GetFromJsonAsync<PagedResult<PageLinkDto>>(LinksPath(seeded.Job.Id, seeded.Root.Id, "?page=1&pageSize=2"), CrawlerApiFactory.Json);
        var second = await client.GetFromJsonAsync<PagedResult<PageLinkDto>>(LinksPath(seeded.Job.Id, seeded.Root.Id, "?page=2&pageSize=2"), CrawlerApiFactory.Json);
        var beyond = await client.GetFromJsonAsync<PagedResult<PageLinkDto>>(LinksPath(seeded.Job.Id, seeded.Root.Id, "?page=3&pageSize=2"), CrawlerApiFactory.Json);

        Assert.Equal([seeded.Crawled.NormalizedUrl, seeded.Uncrawled.NormalizedUrl], first!.Items.Select(l => l.TargetUrl));
        Assert.Equal([ExternalUrl], second!.Items.Select(l => l.TargetUrl));
        Assert.Empty(beyond!.Items);
        Assert.All(new[] { first, second, beyond }, r => Assert.Equal((2, 3), (r.PageSize, r.TotalCount)));
    }

    [Fact]
    public async Task Page_without_outgoing_links_returns_an_empty_page()
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var result = await client.GetFromJsonAsync<PagedResult<PageLinkDto>>(LinksPath(seeded.Job.Id, seeded.Uncrawled.Id), CrawlerApiFactory.Json);

        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=501", "pageSize")]
    public async Task Out_of_range_paging_is_rejected(string query, string field)
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(LinksPath(seeded.Job.Id, seeded.Root.Id, query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CrawlerApiFactory.Json);
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey(field));
    }

    [Fact]
    public async Task Maximum_page_size_is_accepted()
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(LinksPath(seeded.Job.Id, seeded.Root.Id, "?pageSize=500"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_job_unknown_page_and_page_of_another_job_return_404()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        using var client = _factory.CreateClient();

        foreach (var path in new[]
                 {
                     LinksPath(Guid.NewGuid(), seeded.Root.Id),
                     LinksPath(seeded.Job.Id, Guid.NewGuid()),
                     LinksPath(seeded.Job.Id, other.Root.Id),
                 })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CrawlerApiFactory.Json);
            Assert.Equal(404, problem?.Status);
        }
    }

    private static string LinksPath(Guid jobId, Guid pageId, string query = "") => $"/api/jobs/{jobId}/pages/{pageId}/links{query}";

    private sealed record Seeded(CrawlJob Job, CrawlPage Root, CrawlPage Crawled, CrawlPage Uncrawled);

    /// <summary>Root page with three edges: internal crawled (2 occurrences), internal not yet crawled, external.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var job = TestData.Job(JobStatus.Running);
        var root = CrawlPage.CreateRoot(job.Id, new Uri("https://example.com/"));
        root.MarkFetched(200, new LinkMetrics(4, 3, 0.75), job.CreatedAt);
        var crawled = CrawlPage.CreateChild(job.Id, new Uri("https://example.com/a.html"), 1, root.Id, 1);
        crawled.MarkFetched(200, new LinkMetrics(0, 0, 0), job.CreatedAt);
        var uncrawled = CrawlPage.CreateChild(job.Id, new Uri("https://example.com/b.html"), 1, root.Id, 2);

        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            db.Pages.AddRange(root, crawled, uncrawled);
            await db.SaveChangesAsync();

            // One save per edge so identity values follow the insertion order the endpoint must preserve.
            foreach (var edge in new[]
                     {
                         Edge(job.Id, root, crawled.NormalizedUrl, crawled.Id, 2, isInternal: true),
                         Edge(job.Id, root, uncrawled.NormalizedUrl, uncrawled.Id, 1, isInternal: true),
                         Edge(job.Id, root, ExternalUrl, null, 1, isInternal: false),
                     })
            {
                db.Edges.Add(edge);
                await db.SaveChangesAsync();
            }
        });

        return new Seeded(job, root, crawled, uncrawled);
    }

    private static CrawlEdge Edge(Guid jobId, CrawlPage source, string target, Guid? targetPageId, int occurrences, bool isInternal) =>
        new()
        {
            JobId = jobId,
            SourcePageId = source.Id,
            TargetNormalizedUrl = target,
            TargetPageId = targetPageId,
            OccurrenceCount = occurrences,
            IsInternal = isInternal,
        };
}
