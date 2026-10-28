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
/// Fetches one page: bounded retries with exponential backoff for transient failures, manual
/// redirects restricted to the starting host, content-type and size limits enforced while streaming.
/// Destination safety is enforced by <see cref="SafeConnectCallback"/> on every connection.
/// </summary>
public sealed class HttpPageFetcher(
    IHttpClientFactory httpClientFactory,
    IOptions<CrawlerOptions> options,
    IRetryDelay retryDelay,
    ILogger<HttpPageFetcher> logger) : IPageFetcher
{
    public const string AcceptHeader = "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1";

    private const int ReadBufferSize = 16 * 1024;

    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private static readonly HashSet<HttpStatusCode> RedirectStatusCodes =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect,
    ];

    private static readonly HashSet<string> HtmlMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "application/xhtml+xml",
    };

    public async Task<PageFetchResult> FetchAsync(Uri url, string startingHost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || !UrlNormalizer.IsSupportedScheme(url))
        {
            return PageFetchResult.Skip(null, url, "Unsupported URL scheme");
        }

        var http = options.Value.Http;
        var client = httpClientFactory.CreateClient(CrawlingServiceCollectionExtensions.HttpClientName);
        var current = url;

        for (var redirects = 0; ; redirects++)
        {
            var outcome = await SendWithRetriesAsync(client, current, http, cancellationToken);
            if (outcome is AttemptOutcome.Final final)
            {
                return final.Result;
            }

            if (outcome is AttemptOutcome.Transient transient)
            {
                return PageFetchResult.Fail(transient.StatusCode, current, $"{transient.Reason} (gave up after {http.MaxAttempts} attempts)");
            }

            var redirect = (AttemptOutcome.Redirect)outcome;
            if (redirects >= http.MaxRedirects)
            {
                return PageFetchResult.Fail(redirect.StatusCode, current, $"Too many redirects (limit {http.MaxRedirects})");
            }

            if (redirect.Location is null
                || !UrlNormalizer.TryNormalize(redirect.Location.OriginalString, current, out var target))
            {
                return PageFetchResult.Fail(redirect.StatusCode, current, "Redirect without a valid http(s) Location");
            }

            if (!DomainLinkRatio.IsInternal(target, startingHost))
            {
                return PageFetchResult.Skip(redirect.StatusCode, current, $"Redirected to external host {target.Host}");
            }

            logger.LogDebug("Following redirect {StatusCode} from {Url} to {Target}", redirect.StatusCode, current, target);
            current = target;
        }
    }

    private async Task<AttemptOutcome> SendWithRetriesAsync(HttpClient client, Uri url, HttpOptions http, CancellationToken cancellationToken)
    {
        AttemptOutcome outcome = null!;
        for (var attempt = 1; attempt <= http.MaxAttempts; attempt++)
        {
            outcome = await AttemptAsync(client, url, http, cancellationToken);
            if (outcome is not AttemptOutcome.Transient transient)
            {
                return outcome;
            }

            if (attempt < http.MaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(http.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1));
                logger.LogDebug("Transient failure fetching {Url} on attempt {Attempt}: {Reason}; retrying in {Delay}",
                    url, attempt, transient.Reason, delay);
                await retryDelay.WaitAsync(delay, cancellationToken);
            }
        }

        return outcome;
    }

    private async Task<AttemptOutcome> AttemptAsync(HttpClient client, Uri url, HttpOptions http, CancellationToken cancellationToken)
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

            if (TransientStatusCodes.Contains(response.StatusCode))
            {
                return new AttemptOutcome.Transient(status, $"HTTP {status}");
            }

            if (RedirectStatusCodes.Contains(response.StatusCode))
            {
                return new AttemptOutcome.Redirect(status, response.Headers.Location);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AttemptOutcome.Final(PageFetchResult.Fail(status, url, $"HTTP {status} {response.ReasonPhrase}".TrimEnd()));
            }

            return new AttemptOutcome.Final(await ReadHtmlAsync(response, url, http.MaxResponseBytes, attemptCts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AttemptOutcome.Transient(null, $"Timed out after {http.TimeoutSeconds}s");
        }
        catch (Exception ex) when (FindBlocked(ex) is { } blocked)
        {
            return new AttemptOutcome.Final(PageFetchResult.Skip(null, url, blocked.Message));
        }
        catch (HttpRequestException ex)
        {
            return new AttemptOutcome.Transient(null, $"Connection error: {ex.Message}");
        }
        catch (IOException ex)
        {
            // Connection reset while streaming the body.
            return new AttemptOutcome.Transient(null, $"I/O error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (a corrupt gzip/brotli body throws InvalidDataException/InvalidOperationException,
            // a bad charset throws ArgumentException) is a permanent problem with this page, never with the job.
            return new AttemptOutcome.Final(PageFetchResult.Fail(null, url, $"Unreadable response: {ex.GetType().Name}"));
        }
    }

    private static async Task<PageFetchResult> ReadHtmlAsync(HttpResponseMessage response, Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType;
        if (contentType?.MediaType is not { } mediaType || !HtmlMediaTypes.Contains(mediaType))
        {
            return PageFetchResult.Skip(status, url, $"Unsupported content type '{contentType?.MediaType ?? "none"}'");
        }

        var tooLarge = $"Response exceeded {maxBytes} bytes";
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return PageFetchResult.Skip(status, url, tooLarge);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, ReadBufferSize), cancellationToken)) > 0)
            {
                // The limit applies to decompressed bytes, which also bounds compression bombs.
                if (body.Length + read > maxBytes)
                {
                    return PageFetchResult.Skip(status, url, tooLarge);
                }

                body.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var html = ResolveEncoding(contentType.CharSet).GetString(body.GetBuffer(), 0, (int)body.Length);
        return PageFetchResult.Ok(status, html, url);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
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

    private abstract record AttemptOutcome
    {
        public sealed record Final(PageFetchResult Result) : AttemptOutcome;

        public sealed record Transient(int? StatusCode, string Reason) : AttemptOutcome;

        public sealed record Redirect(int StatusCode, Uri? Location) : AttemptOutcome;
    }
}
