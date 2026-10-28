using System.Net;
using System.Net.Http.Json;
using Crawler.Api.Tests.Infrastructure;
using Crawler.Core.Application.Contracts;
using Crawler.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Crawler.Api.Tests;

/// <summary>Test plan A7–A8. Own database so the total count is exact.</summary>
[Collection(InfrastructureCollection.Name)]
public sealed class JobListTests(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    private const int SeededJobs = 25;
    private readonly CrawlerApiFactory _factory = infrastructure.Factory("job_list_tests");
    private List<CrawlJob> _expectedOrder = [];

    public async Task InitializeAsync()
    {
        _expectedOrder = await _factory.WithDbAsync(async db =>
        {
            if (db.Jobs.Any())
            {
                return db.Jobs.ToList();
            }

            var start = TestData.UtcNow().AddHours(-1);
            // Pairs share a timestamp so the id tie-breaker is exercised as well.
            var jobs = Enumerable.Range(0, SeededJobs)
                .Select(i => TestData.Job(JobStatus.Completed, url: $"https://example.com/{i}", createdAt: start.AddMinutes(i / 2)))
                .ToList();
            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync();
            return jobs;
        });

        // PostgreSQL orders uuid values by their canonical text form.
        _expectedOrder = _expectedOrder
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A7_first_page_holds_the_newest_jobs()
    {
        using var client = _factory.CreateClient();

        var result = await client.GetFromJsonAsync<PagedResult<JobSummaryDto>>("/api/jobs?page=1&pageSize=10", CrawlerApiFactory.Json);

        Assert.NotNull(result);
        Assert.Equal(SeededJobs, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(_expectedOrder.Take(10).Select(j => j.Id), result.Items.Select(i => i.JobId));
        var first = result.Items[0];
        var expected = _expectedOrder[0];
        Assert.Equal(expected.InputUrl, first.InputUrl);
        Assert.Equal(JobStatus.Completed, first.Status);
        Assert.Equal(expected.CreatedAt, first.CreatedAt);
        Assert.Equal(expected.MaxDepth, first.MaxDepth);
    }

    [Fact]
    public async Task A7_last_page_holds_the_remainder()
    {
        using var client = _factory.CreateClient();

        var result = await client.GetFromJsonAsync<PagedResult<JobSummaryDto>>("/api/jobs?page=3&pageSize=10", CrawlerApiFactory.Json);

        Assert.NotNull(result);
        Assert.Equal(SeededJobs, result.TotalCount);
        Assert.Equal(_expectedOrder.Skip(20).Select(j => j.Id), result.Items.Select(i => i.JobId));
    }

    [Fact]
    public async Task Defaults_are_page_1_and_page_size_20()
    {
        using var client = _factory.CreateClient();

        var result = await client.GetFromJsonAsync<PagedResult<JobSummaryDto>>("/api/jobs", CrawlerApiFactory.Json);

        Assert.NotNull(result);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(20, result.Items.Count);
    }

    [Theory]
    [InlineData("pageSize=500", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task A8_out_of_range_paging_is_rejected(string query, string field)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CrawlerApiFactory.Json);
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey(field));
    }
}
