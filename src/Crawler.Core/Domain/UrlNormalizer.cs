using System.Diagnostics.CodeAnalysis;

namespace Crawler.Core.Domain;

/// <summary>
/// Produces the canonical form used as the de-duplication key within a job.
/// Deliberately conservative: only scheme/host case, default ports, fragments and empty paths are
/// normalized. Path case, query order and trailing slashes are meaningful on the web and are kept.
/// </summary>
public static class UrlNormalizer
{
    public static bool IsSupportedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    public static bool TryNormalize(string? href, Uri? baseUri, [NotNullWhen(true)] out Uri? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var trimmed = href.Trim();
        Uri? candidate;
        var created = baseUri is null
            ? Uri.TryCreate(trimmed, UriKind.Absolute, out candidate)
            : Uri.TryCreate(baseUri, trimmed, out candidate);

        if (!created || candidate is null || !candidate.IsAbsoluteUri || !IsSupportedScheme(candidate) || candidate.Host.Length == 0)
        {
            return false;
        }

        normalized = Normalize(candidate);
        return true;
    }

    public static Uri Normalize(Uri absolute)
    {
        if (!absolute.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute URI is required.", nameof(absolute));
        }

        // Credentials embedded in a URL are never sent by the fetcher, must not be stored or displayed, and
        // must not make the same page look like two different resources.
        var builder = new UriBuilder(absolute)
        {
            Scheme = absolute.Scheme.ToLowerInvariant(),
            Host = absolute.Host.ToLowerInvariant(),
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };

        if (absolute.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }
}
