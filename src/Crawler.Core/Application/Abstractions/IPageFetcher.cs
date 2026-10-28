namespace Crawler.Core.Application.Abstractions;

public enum PageFetchOutcome
{
    /// <summary>HTML body available in <see cref="PageFetchResult.Html"/>.</summary>
    Html,
    /// <summary>Non-HTML content type, external redirect, oversized body, or blocked destination.</summary>
    Skipped,
    /// <summary>Non-success HTTP status or exhausted retries.</summary>
    Failed,
}

/// <param name="FinalUrl">URL after same-host redirects; equals the requested URL when none were followed.</param>
public sealed record PageFetchResult(
    PageFetchOutcome Outcome,
    int? HttpStatusCode,
    string? Html,
    Uri FinalUrl,
    string? Error)
{
    public static PageFetchResult Ok(int statusCode, string html, Uri finalUrl) =>
        new(PageFetchOutcome.Html, statusCode, html, finalUrl, null);

    public static PageFetchResult Skip(int? statusCode, Uri url, string reason) =>
        new(PageFetchOutcome.Skipped, statusCode, null, url, reason);

    public static PageFetchResult Fail(int? statusCode, Uri url, string error) =>
        new(PageFetchOutcome.Failed, statusCode, null, url, error);
}

public interface IPageFetcher
{
    /// <summary>
    /// Fetches an HTML page with timeouts, bounded retries for transient failures, manual redirect
    /// handling restricted to <paramref name="startingHost"/>, and destination safety validation.
    /// Never throws for HTTP-level problems; only for cancellation.
    /// </summary>
    Task<PageFetchResult> FetchAsync(Uri url, string startingHost, CancellationToken cancellationToken);
}
