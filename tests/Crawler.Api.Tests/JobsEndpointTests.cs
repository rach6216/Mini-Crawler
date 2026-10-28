using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crawler.Api.Tests.Infrastructure;
using Crawler.Core.Application.Contracts;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Tests;

/// <summary>Test plan section 6: A1–A6, A9–A12.</summary>
[Collection(InfrastructureCollection.Name)]
public sealed class JobsEndpointTests(InfrastructureFixture infrastructure)
{
    private readonly CrawlerApiFactory _factory = infrastructure.Factory("jobs_endpoint_tests");

    [Fact]
    public async Task A1_valid_request_creates_pending_job_and_outbox_row()
    {
        using var client = _factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/jobs", """{ "url": "https://Example.com/a#x" }""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateJobResponse>(CrawlerApiFactory.Json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.JobId);
        Assert.Equal($"/api/jobs/{body.JobId}", response.Headers.Location?.OriginalString);

        var (job, outbox) = await _factory.WithDbAsync(async db => (
            await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == body.JobId),
            await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.JobId == body.JobId)));

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(2, job.MaxDepth);
        Assert.Equal(200, job.MaxPages);
        Assert.Equal("https://Example.com/a#x", job.InputUrl);
        Assert.Equal("https://example.com/a", job.NormalizedStartUrl);
        Assert.Equal("example.com", job.StartingHost);

        Assert.Equal(OutboxMessage.CrawlJobRequestedType, outbox.MessageType);
        Assert.Null(outbox.PublishedAt);
        var message = JsonSerializer.Deserialize<CrawlJobMessage>(outbox.Payload, RabbitMqJobMessagePublisher.SerializerOptions);
        Assert.NotNull(message);
        Assert.Equal(body.JobId, message.JobId);
        Assert.Equal(job.CorrelationId, message.CorrelationId);
        Assert.Equal(CrawlJobMessage.CurrentSchemaVersion, message.SchemaVersion);
    }

    [Fact]
    public async Task A1_explicit_limits_are_stored()
    {
        using var client = _factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/jobs", """{ "url": "https://example.com", "maxDepth": 0, "maxPages": 1000 }""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateJobResponse>(CrawlerApiFactory.Json);
        var job = await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == body!.JobId));
        Assert.Equal(0, job.MaxDepth);
        Assert.Equal(1000, job.MaxPages);
    }

    [Theory]
    [InlineData("""{ "url": "ftp://x" }""")]
    [InlineData("""{ "url": "notaurl" }""")]
    [InlineData("""{ "url": "" }""")]
    [InlineData("""{ }""")]
    public async Task A2_invalid_url_is_rejected(string json)
    {
        await AssertValidationErrorAsync(json, "url");
    }

    [Theory]
    [InlineData("""{ "url": "https://example.com", "maxDepth": 9 }""", "maxDepth")]
    [InlineData("""{ "url": "https://example.com", "maxDepth": -1 }""", "maxDepth")]
    [InlineData("""{ "url": "https://example.com", "maxPages": 0 }""", "maxPages")]
    [InlineData("""{ "url": "https://example.com", "maxPages": 5000 }""", "maxPages")]
    public async Task A3_out_of_range_limits_are_rejected(string json, string field)
    {
        await AssertValidationErrorAsync(json, field);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/")]
    public async Task A4_private_and_loopback_hosts_are_rejected(string url)
    {
        await AssertValidationErrorAsync(JsonSerializer.Serialize(new { url }), "url");
    }

    [Fact]
    public async Task A2_invalid_request_does_not_create_rows()
    {
        using var client = _factory.CreateClient();
        var marker = $"ftp://{Guid.NewGuid():N}.example";

        await PostJsonAsync(client, "/api/jobs", JsonSerializer.Serialize(new { url = marker }));

        var count = await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.InputUrl == marker));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A5_unknown_job_returns_404_problem()
    {
        using var client = _factory.CreateClient();

        foreach (var path in new[] { $"/api/jobs/{Guid.NewGuid()}", $"/api/jobs/{Guid.NewGuid()}/tree" })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CrawlerApiFactory.Json);
            Assert.Equal(404, problem?.Status);
        }
    }

    [Fact]
    public async Task A6_existing_job_returns_all_details()
    {
        var job = TestData.Job(JobStatus.Running, url: "https://Example.com/a#x");
        job.NormalizedStartUrl = "https://example.com/a";
        job.DiscoveredPages = 12;
        job.ProcessedPages = 5;
        job.FailedPages = 1;
        await SeedAsync(job);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs/{job.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var json = document.RootElement;
        Assert.Equal(job.Id, json.GetProperty("jobId").GetGuid());
        Assert.Equal("https://Example.com/a#x", json.GetProperty("inputUrl").GetString());
        Assert.Equal("https://example.com/a", json.GetProperty("normalizedStartUrl").GetString());
        Assert.Equal("example.com", json.GetProperty("startingHost").GetString());
        Assert.Equal("Running", json.GetProperty("status").GetString());
        Assert.EndsWith("Z", json.GetProperty("createdAt").GetString());
        Assert.Equal(job.CreatedAt, json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(job.StartedAt, json.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("failureReason").ValueKind);
        Assert.Equal(2, json.GetProperty("maxDepth").GetInt32());
        Assert.Equal(200, json.GetProperty("maxPages").GetInt32());
        Assert.Equal(12, json.GetProperty("discoveredPages").GetInt32());
        Assert.Equal(5, json.GetProperty("processedPages").GetInt32());
        Assert.Equal(1, json.GetProperty("failedPages").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.False(json.GetProperty("cancellationRequested").GetBoolean());
        Assert.Equal(41, json.GetProperty("progressPercent").GetInt32());
    }

    [Fact]
    public async Task A6_terminal_job_reports_full_progress()
    {
        var job = TestData.Job(JobStatus.Failed);
        job.FailureReason = "boom";
        job.DiscoveredPages = 10;
        job.ProcessedPages = 1;
        await SeedAsync(job);
        using var client = _factory.CreateClient();

        var details = await client.GetFromJsonAsync<JobDetailsDto>($"/api/jobs/{job.Id}", CrawlerApiFactory.Json);

        Assert.NotNull(details);
        Assert.Equal(JobStatus.Failed, details.Status);
        Assert.Equal("boom", details.FailureReason);
        Assert.Equal(100, details.ProgressPercent);
    }

    [Fact]
    public async Task A9_tree_is_nested_in_discovery_order_without_duplicates()
    {
        var job = TestData.Job(JobStatus.Completed);
        var root = CrawlPage.CreateRoot(job.Id, new Uri("https://example.com/"));
        root.MarkFetched(200, new LinkMetrics(4, 3, 0.75), job.CreatedAt);
        var a = CrawlPage.CreateChild(job.Id, new Uri("https://example.com/a"), 1, root.Id, 1);
        var b = CrawlPage.CreateChild(job.Id, new Uri("https://example.com/b"), 1, root.Id, 2);
        var c = CrawlPage.CreateChild(job.Id, new Uri("https://example.com/c"), 2, a.Id, 3);
        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            db.Pages.AddRange(root, b, a, c);
            db.Edges.AddRange(
                Edge(job.Id, root, a), Edge(job.Id, root, b), Edge(job.Id, a, c),
                Edge(job.Id, b, a),     // repeated reference to an already discovered page
                Edge(job.Id, c, root)); // cycle back to the root
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs/{job.Id}/tree");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tree = await response.Content.ReadFromJsonAsync<JobTreeDto>(CrawlerApiFactory.Json);
        Assert.NotNull(tree);
        Assert.Equal(job.Id, tree.JobId);
        Assert.Equal(JobStatus.Completed, tree.Status);
        Assert.Equal(4, tree.TotalNodes);
        Assert.NotNull(tree.Root);
        Assert.Equal(root.Id, tree.Root.PageId);
        Assert.Equal(PageFetchStatus.Fetched, tree.Root.FetchStatus);
        Assert.Equal(0.75, tree.Root.DomainLinkRatio);
        Assert.Equal([a.Id, b.Id], tree.Root.Children.Select(n => n.PageId));
        Assert.Equal([c.Id], tree.Root.Children[0].Children.Select(n => n.PageId));
        Assert.Empty(tree.Root.Children[1].Children);
        Assert.Empty(tree.Root.Children[0].Children[0].Children);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"fetchStatus\":\"Fetched\"", raw);
        Assert.Contains("\"fetchStatus\":\"Discovered\"", raw);
    }

    [Fact]
    public async Task A10_tree_of_pending_job_has_null_root()
    {
        var job = await SeedAsync(TestData.Job(JobStatus.Pending));
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/jobs/{job.Id}/tree");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("root").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("totalNodes").GetInt32());
        Assert.Equal("Pending", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A11_cancel_matrix()
    {
        var pending = await SeedAsync(TestData.Job(JobStatus.Pending));
        var running = await SeedAsync(TestData.Job(JobStatus.Running));
        var completed = await SeedAsync(TestData.Job(JobStatus.Completed));
        var failed = await SeedAsync(TestData.Job(JobStatus.Failed));
        using var client = _factory.CreateClient();

        var pendingResponse = await client.PostAsync($"/api/jobs/{pending.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        var pendingBody = await pendingResponse.Content.ReadFromJsonAsync<CancelJobResponse>(CrawlerApiFactory.Json);
        Assert.Equal(new CancelJobResponse(pending.Id, JobStatus.Canceled, true), pendingBody);

        var runningResponse = await client.PostAsync($"/api/jobs/{running.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, runningResponse.StatusCode);
        Assert.Equal(new CancelJobResponse(running.Id, JobStatus.Running, true),
            await runningResponse.Content.ReadFromJsonAsync<CancelJobResponse>(CrawlerApiFactory.Json));

        var repeatResponse = await client.PostAsync($"/api/jobs/{running.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, repeatResponse.StatusCode);
        Assert.Equal(new CancelJobResponse(running.Id, JobStatus.Running, true),
            await repeatResponse.Content.ReadFromJsonAsync<CancelJobResponse>(CrawlerApiFactory.Json));

        var canceledResponse = await client.PostAsync($"/api/jobs/{pending.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, canceledResponse.StatusCode);
        Assert.Equal(new CancelJobResponse(pending.Id, JobStatus.Canceled, true),
            await canceledResponse.Content.ReadFromJsonAsync<CancelJobResponse>(CrawlerApiFactory.Json));

        foreach (var terminal in new[] { completed, failed })
        {
            var conflict = await client.PostAsync($"/api/jobs/{terminal.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
            var problem = await conflict.Content.ReadFromJsonAsync<ProblemDetails>(CrawlerApiFactory.Json);
            Assert.Equal($"Job is already {terminal.Status}.", problem?.Detail);
        }

        var unknown = await client.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A12_liveness_is_healthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        using var client = _factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/jobs", """{ "url": "https://example.com", "maxDepth": "deep" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cors_allows_the_frontend_dev_origin()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/jobs");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("http://localhost:5173", Assert.Single(origins));
    }

    private async Task AssertValidationErrorAsync(string json, string field)
    {
        using var client = _factory.CreateClient();

        var response = await PostJsonAsync(client, "/api/jobs", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(CrawlerApiFactory.Json);
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.True(problem.Errors.ContainsKey(field), $"Expected an error for '{field}', got: {string.Join(", ", problem.Errors.Keys)}");
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<CrawlJob> SeedAsync(CrawlJob job)
    {
        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        });
        return job;
    }

    private static CrawlEdge Edge(Guid jobId, CrawlPage source, CrawlPage target) =>
        new()
        {
            JobId = jobId,
            SourcePageId = source.Id,
            TargetNormalizedUrl = target.NormalizedUrl,
            TargetPageId = target.Id,
            OccurrenceCount = 1,
            IsInternal = true,
        };
}
