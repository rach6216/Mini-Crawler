using AngleSharp.Html.Parser;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;

namespace Crawler.Core.Infrastructure.Crawling;

public sealed class AngleSharpLinkExtractor : ILinkExtractor
{
    public IReadOnlyList<Uri> Extract(string html, Uri documentUrl)
    {
        ArgumentNullException.ThrowIfNull(documentUrl);
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        // HtmlParser is not documented as thread-safe; a parser per call is cheap and keeps the extractor a singleton.
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var baseUri = ResolveBase(document.QuerySelector("base[href]")?.GetAttribute("href"), documentUrl);

        var links = new List<Uri>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (UrlNormalizer.TryNormalize(anchor.GetAttribute("href"), baseUri, out var normalized))
            {
                links.Add(normalized);
            }
        }

        return links;
    }

    // Only the first <base href> counts (HTML spec); an unusable value falls back to the document URL.
    private static Uri ResolveBase(string? baseHref, Uri documentUrl)
    {
        if (string.IsNullOrWhiteSpace(baseHref)
            || !Uri.TryCreate(documentUrl, baseHref.Trim(), out var candidate)
            || !UrlNormalizer.IsSupportedScheme(candidate))
        {
            return documentUrl;
        }

        return candidate;
    }
}
