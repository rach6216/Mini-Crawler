using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Options;
using Crawler.Core.Infrastructure.Crawling;
using Crawler.Worker.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawler.Worker.Tests;

/// <summary>
/// Test plan §7 W7–W11 against a real Kestrel server on 127.0.0.1 through the production HttpClient
/// pipeline (named client, SocketsHttpHandler, SSRF connect callback). Only DNS and the backoff wait are faked.
/// </summary>
public sealed class HttpPageFetcherTests : IAsyncLifetime
{
    private const string TrustedLoopback = "127.0.0.1";

    private TestSiteServer _server = null!;

    public async Task InitializeAsync() => _server = await TestSiteServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Fetches_fixture_page_as_html()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/"));

        Assert.Equal(PageFetchOutcome.Html, result.Outcome);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Contains("Fixture site", result.Html);
        Assert.Equal(_server.Url("/"), result.FinalUrl);
    }

    [Fact]
    public async Task Sends_configured_user_agent_and_accept_and_never_cookies_or_auth()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/echo-headers"));

        Assert.Contains($"<p id=\"ua\">{new HttpOptions().UserAgent}</p>", result.Html);
        Assert.Contains($"<p id=\"accept\">{HttpPageFetcher.AcceptHeader}</p>", result.Html);
        Assert.Contains("<p id=\"cookie\">0</p>", result.Html);
        Assert.Contains("<p id=\"auth\">0</p>", result.Html);
    }

    [Fact]
    public async Task W7_retries_transient_503_with_exponential_backoff_then_succeeds()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/flaky"));

        Assert.Equal(PageFetchOutcome.Html, result.Outcome);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(3, _server.Hits("/flaky"));
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], fetcher.Delays);
    }

    [Fact]
    public async Task W7_not_found_is_not_retried()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/d.html"));

        Assert.Equal(PageFetchOutcome.Failed, result.Outcome);
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Equal(1, _server.Hits("/d.html"));
        Assert.Empty(fetcher.Delays);
    }

    [Fact]
    public async Task Other_client_errors_are_not_retried()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/forbidden"));

        Assert.Equal((PageFetchOutcome.Failed, 403), (result.Outcome, result.HttpStatusCode));
        Assert.Equal(1, _server.Hits("/forbidden"));
    }

    [Fact]
    public async Task W7_exhausted_transient_status_fails_with_last_status()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/always-503"));

        Assert.Equal((PageFetchOutcome.Failed, 503), (result.Outcome, result.HttpStatusCode));
        Assert.Equal(3, _server.Hits("/always-503"));
        Assert.Contains("3 attempts", result.Error);
    }

    [Fact]
    public async Task W7_timeout_on_every_attempt_fails()
    {
        using var fetcher = CreateFetcher(settings: new() { ["Crawler:Http:TimeoutSeconds"] = "1" });

        var result = await fetcher.FetchAsync(_server.Url("/slow"));

        Assert.Equal(PageFetchOutcome.Failed, result.Outcome);
        Assert.Null(result.HttpStatusCode);
        Assert.Contains("Timed out", result.Error);
        Assert.Equal(3, _server.Hits("/slow"));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_reported_as_timeout()
    {
        using var fetcher = CreateFetcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.Fetcher.FetchAsync(_server.Url("/slow"), TrustedLoopback, cts.Token));
    }

    [Fact]
    public async Task W8_loopback_literal_is_blocked_before_connect_when_not_trusted()
    {
        using var fetcher = CreateFetcher(trustedHosts: []);

        var result = await fetcher.FetchAsync(_server.Url("/index.html"));

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Contains("blocked", result.Error);
        Assert.Equal(0, _server.TotalHits);
    }

    [Theory]
    [InlineData("internal.test", "10.1.2.3")]
    [InlineData("metadata.test", "169.254.169.254")]
    [InlineData("mixed.test", "93.184.216.34,192.168.1.10")]
    [InlineData("v6.test", "::1")]
    public async Task W8_host_resolving_to_non_public_address_is_blocked(string host, string addresses)
    {
        using var fetcher = CreateFetcher(trustedHosts: []);
        fetcher.Dns.Map(host, addresses.Split(',').Select(IPAddress.Parse).ToArray());

        var result = await fetcher.Fetcher.FetchAsync(new Uri($"http://{host}:{_server.Port}/index.html"), host, CancellationToken.None);

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Contains("non-public address", result.Error);
        Assert.Contains(host, fetcher.Dns.Queries);
        Assert.Equal(0, _server.TotalHits);
        Assert.Empty(fetcher.Delays);
    }

    [Fact]
    public async Task Trusted_host_connects_to_the_address_resolved_inside_the_callback()
    {
        using var fetcher = CreateFetcher(trustedHosts: ["fixture.test"]);
        fetcher.Dns.Map("fixture.test", IPAddress.Loopback);

        var result = await fetcher.Fetcher.FetchAsync(new Uri($"http://fixture.test:{_server.Port}/a.html"), "fixture.test", CancellationToken.None);

        Assert.Equal(PageFetchOutcome.Html, result.Outcome);
        Assert.Contains("Page A", result.Html);
        Assert.Equal(["fixture.test"], fetcher.Dns.Queries);
    }

    [Fact]
    public async Task W9_redirect_to_another_host_is_skipped_and_not_followed()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/redirect-external"));

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Equal(302, result.HttpStatusCode);
        Assert.Contains("Redirected to external host localhost", result.Error);
        Assert.Equal(0, _server.Hits("/never"));
    }

    [Fact]
    public async Task Same_host_redirect_is_followed_and_reports_final_url()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/redirect-same-host"));

        Assert.Equal(PageFetchOutcome.Html, result.Outcome);
        Assert.Equal(_server.Url("/a.html"), result.FinalUrl);
        Assert.Contains("Page A", result.Html);
    }

    [Fact]
    public async Task Redirect_loop_fails_after_max_redirects()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/redirect-loop"));

        Assert.Equal(PageFetchOutcome.Failed, result.Outcome);
        Assert.Contains("Too many redirects", result.Error);
        Assert.Equal(new HttpOptions().MaxRedirects + 1, _server.Hits("/redirect-loop"));
    }

    [Fact]
    public async Task Redirect_to_non_http_location_fails()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/redirect-mailto"));

        Assert.Equal(PageFetchOutcome.Failed, result.Outcome);
        Assert.Contains("Location", result.Error);
    }

    [Fact]
    public async Task W10_non_html_content_type_is_skipped_without_reading_the_body()
    {
        using var fetcher = CreateFetcher(settings: new() { ["Crawler:Http:TimeoutSeconds"] = "5" });
        var stopwatch = Stopwatch.StartNew();

        var result = await fetcher.FetchAsync(_server.Url("/pdf"));

        // The body never completes, so returning before the timeout proves it was not read.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"took {stopwatch.Elapsed}");
        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Null(result.Html);
        Assert.Contains("application/pdf", result.Error);
        Assert.Equal(1, _server.Hits("/pdf"));
    }

    [Fact]
    public async Task Fixture_image_is_skipped_as_non_html()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/e.png"));

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Contains("image/png", result.Error);
    }

    [Theory]
    [InlineData("/big")]
    [InlineData("/big-declared")]
    public async Task W11_body_larger_than_limit_is_skipped_with_reason(string path)
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url(path));

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Null(result.Html);
        Assert.Equal($"Response exceeded {TestSiteServer.BodyLimit} bytes", result.Error);
    }

    [Fact]
    public async Task Decodes_body_with_response_charset()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(_server.Url("/latin1"));

        Assert.Equal("<p>café</p>", result.Html);
    }

    [Fact]
    public async Task Unsupported_scheme_is_skipped_without_a_request()
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.Fetcher.FetchAsync(new Uri("ftp://127.0.0.1/file"), TrustedLoopback, CancellationToken.None);

        Assert.Equal(PageFetchOutcome.Skipped, result.Outcome);
        Assert.Equal(0, _server.TotalHits);
    }

    private static FetcherUnderTest CreateFetcher(string[]? trustedHosts = null, Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Crawler:Http:MaxResponseBytes"] = TestSiteServer.BodyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Crawler:Http:TimeoutSeconds"] = "5",
        };
        var hosts = trustedHosts ?? [TrustedLoopback];
        for (var i = 0; i < hosts.Length; i++)
        {
            values[$"Crawler:Security:TrustedHosts:{i}"] = hosts[i];
        }

        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var dns = new FakeDnsResolver();
        var delay = new RecordingRetryDelay();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<CrawlerOptions>().Bind(configuration.GetSection(CrawlerOptions.SectionName));
        services.AddCrawling(configuration);
        services.RemoveAll<IDnsResolver>();
        services.AddSingleton<IDnsResolver>(dns);
        services.RemoveAll<IRetryDelay>();
        services.AddSingleton<IRetryDelay>(delay);

        return new FetcherUnderTest(services.BuildServiceProvider(), dns, delay);
    }

    private sealed class FetcherUnderTest(ServiceProvider provider, FakeDnsResolver dns, RecordingRetryDelay delay) : IDisposable
    {
        public IPageFetcher Fetcher { get; } = provider.GetRequiredService<IPageFetcher>();

        public FakeDnsResolver Dns { get; } = dns;

        public IReadOnlyList<TimeSpan> Delays => delay.Delays;

        public Task<PageFetchResult> FetchAsync(Uri url) => Fetcher.FetchAsync(url, url.Host, CancellationToken.None);

        public void Dispose() => provider.Dispose();
    }

    private sealed class FakeDnsResolver : IDnsResolver
    {
        private readonly ConcurrentDictionary<string, IPAddress[]> _records = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _queries = new();

        public IReadOnlyList<string> Queries => [.. _queries];

        public void Map(string host, params IPAddress[] addresses) => _records[host] = addresses;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            _queries.Enqueue(host);
            return _records.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
        }
    }

    private sealed class RecordingRetryDelay : IRetryDelay
    {
        private readonly ConcurrentQueue<TimeSpan> _delays = new();

        public IReadOnlyList<TimeSpan> Delays => [.. _delays];

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delays.Enqueue(delay);
            return Task.CompletedTask;
        }
    }
}
