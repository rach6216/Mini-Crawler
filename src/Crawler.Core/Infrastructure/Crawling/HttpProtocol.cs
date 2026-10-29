using System.Net;
using Crawler.Core.Application.Options;

namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>HTTP protocol constants and the retry backoff shared by the page fetcher and the robots.txt provider.</summary>
internal static class HttpProtocol
{
    public const string UserAgentHeader = "User-Agent";
    public const string AcceptHeader = "Accept";

    /// <summary>Lowest status code of the 5xx (server error) class.</summary>
    public const int FirstServerErrorStatus = 500;

    /// <summary>Redirects followed manually so that every hop is checked against the starting host.</summary>
    public static readonly IReadOnlySet<HttpStatusCode> RedirectStatusCodes = new HashSet<HttpStatusCode>
    {
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect,
    };

    /// <summary>Delay before retry number <paramref name="failedAttempt"/> + 1: base × multiplier^(failedAttempt − 1).</summary>
    public static TimeSpan RetryDelay(HttpOptions http, int failedAttempt) =>
        TimeSpan.FromMilliseconds(http.RetryBaseDelayMilliseconds * Math.Pow(http.RetryBackoffMultiplier, failedAttempt - 1));
}
