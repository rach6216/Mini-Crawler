using System.Buffers;
using System.Net;
using System.Text;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>
/// Fetches robots.txt through the same SSRF-safe named client as <see cref="HttpPageFetcher"/> and maps the
/// outcome per RFC 9309: 2xx → parsed rules, 4xx / unusable redirect → allow all, 5xx / timeout /
/// connection failure / blocked destination (after the configured retries) → everything disallowed.
/// </summary>
public sealed class RobotsPolicyProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<CrawlerOptions> options,
    IRetryDelay retryDelay,
    ILogger<RobotsPolicyProvider> logger) : IRobotsPolicyProvider
{
    /// <summary>RFC 9309 §2.5: crawlers must parse at least 500 KiB; content beyond is ignored.</summary>
    public const int MaxRobotsBytes = 500 * 1024;

    public const string AcceptHeader = "text/plain,*/*;q=0.1";

    private const int ReadBufferSize = 16 * 1024;

    private static readonly char[] ProductTokenSeparators = ['/', ' '];

    private static readonly HashSet<HttpStatusCode> RedirectStatusCodes =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect,
    ];

    public async Task<RobotsDecision> GetAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUrl);
        var http = options.Value.Http;
        if (!http.RespectRobotsTxt)
        {
            logger.LogInformation("robots.txt for {Host} not consulted: RespectRobotsTxt is disabled", startUrl.Host);
            return RobotsDecision.AllowAll;
        }

        var outcome = await FetchAsync(new Uri(startUrl, "/robots.txt"), startUrl.Host, http, cancellationToken);
        switch (outcome)
        {
            case Outcome.Content content:
                var rules = RobotsRules.Parse(content.Body, ProductToken(http.UserAgent));
                logger.LogInformation("robots.txt for {Host}: {RuleCount} rules, group {Group}",
                    startUrl.Host, rules.RuleCount, rules.Group ?? "none");
                return RobotsDecision.FromRules(rules);

            case Outcome.Missing missing:
                logger.LogInformation("robots.txt for {Host}: {Reason}, all pages allowed", startUrl.Host, missing.Reason);
                return RobotsDecision.AllowAll;

            default:
                var reason = ((Outcome.Unreachable)outcome).Reason;
                logger.LogWarning("robots.txt for {Host} unavailable ({Reason}); every page of this job is disallowed", startUrl.Host, reason);
                return RobotsDecision.Unavailable(reason);
        }
    }

    /// <summary>The product token is the User-Agent's leading product name, e.g. <c>CrawlerAssignmentBot</c>.</summary>
    public static string ProductToken(string userAgent)
    {
        var token = userAgent.Split(ProductTokenSeparators, 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrEmpty(token) ? RobotsRules.WildcardAgent : token;
    }

    private async Task<Outcome> FetchAsync(Uri robotsUrl, string startingHost, HttpOptions http, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(CrawlingServiceCollectionExtensions.HttpClientName);
        var current = robotsUrl;

        for (var redirects = 0; ; redirects++)
        {
            var outcome = await SendWithRetriesAsync(client, current, http, cancellationToken);
            if (outcome is not Outcome.Redirect redirect)
            {
                return outcome;
            }

            // RFC 9309 §2.3.1.2: a robots.txt that cannot be reached through redirects counts as unavailable (allow all).
            if (redirects >= http.MaxRedirects)
            {
                return new Outcome.Missing($"too many redirects (limit {http.MaxRedirects})");
            }

            if (redirect.Location is null || !UrlNormalizer.TryNormalize(redirect.Location.OriginalString, current, out var target))
            {
                return new Outcome.Missing($"HTTP {redirect.StatusCode} without a valid Location");
            }

            if (!DomainLinkRatio.IsInternal(target, startingHost))
            {
                return new Outcome.Missing($"redirected to external host {target.Host}");
            }

            current = target;
        }
    }

    private async Task<Outcome> SendWithRetriesAsync(HttpClient client, Uri url, HttpOptions http, CancellationToken cancellationToken)
    {
        Outcome outcome = null!;
        for (var attempt = 1; attempt <= http.MaxAttempts; attempt++)
        {
            outcome = await AttemptAsync(client, url, http, cancellationToken);
            if (outcome is not Outcome.Unreachable { Retryable: true } unreachable)
            {
                return outcome;
            }

            if (attempt < http.MaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(http.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1));
                logger.LogDebug("Transient failure fetching {Url} on attempt {Attempt}: {Reason}; retrying in {Delay}",
                    url, attempt, unreachable.Reason, delay);
                await retryDelay.WaitAsync(delay, cancellationToken);
            }
        }

        return outcome;
    }

    private static async Task<Outcome> AttemptAsync(HttpClient client, Uri url, HttpOptions http, CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(http.TimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", http.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", AcceptHeader);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
            var status = (int)response.StatusCode;

            if (RedirectStatusCodes.Contains(response.StatusCode))
            {
                return new Outcome.Redirect(status, response.Headers.Location);
            }

            if (status >= 500)
            {
                return new Outcome.Unreachable($"HTTP {status}", Retryable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Outcome.Missing($"HTTP {status}");
            }

            return new Outcome.Content(await ReadTruncatedAsync(response, attemptCts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Outcome.Unreachable($"timed out after {http.TimeoutSeconds}s", Retryable: true);
        }
        catch (Exception ex) when (FindBlocked(ex) is { } blocked)
        {
            return new Outcome.Unreachable(blocked.Message, Retryable: false);
        }
        catch (HttpRequestException ex)
        {
            return new Outcome.Unreachable($"connection error: {ex.Message}", Retryable: true);
        }
        catch (IOException ex)
        {
            return new Outcome.Unreachable($"I/O error: {ex.Message}", Retryable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A corrupt compressed body: the server answered but the file cannot be read.
            return new Outcome.Unreachable($"unreadable response: {ex.GetType().Name}", Retryable: false);
        }
    }

    private static async Task<string> ReadTruncatedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var truncated = false;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ReadBufferSize), cancellationToken)) > 0)
            {
                var take = (int)Math.Min(read, MaxRobotsBytes - body.Length);
                body.Write(buffer, 0, take);
                if (take < read)
                {
                    truncated = true;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var bytes = body.GetBuffer();
        var length = (int)body.Length;
        if (truncated)
        {
            // Drop the partial last line so a cut-off pattern is not read as a shorter, broader one.
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n', length - 1);
            length = lastNewline + 1;
        }

        // RFC 9309 §2.3: robots.txt is UTF-8.
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static BlockedDestinationException? FindBlocked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BlockedDestinationException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    private abstract record Outcome
    {
        public sealed record Content(string Body) : Outcome;

        public sealed record Missing(string Reason) : Outcome;

        public sealed record Unreachable(string Reason, bool Retryable) : Outcome;

        public sealed record Redirect(int StatusCode, Uri? Location) : Outcome;
    }
}
