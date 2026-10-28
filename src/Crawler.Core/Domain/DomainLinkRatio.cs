namespace Crawler.Core.Domain;

public readonly record struct LinkMetrics(int TotalLinks, int InternalLinks, double Ratio);

public static class DomainLinkRatio
{
    /// <summary>
    /// Ratio = internal eligible link occurrences / total eligible link occurrences. Duplicates count
    /// as separate occurrences. Links with unsupported schemes are ignored defensively even though the
    /// extractor already filters them. Zero eligible links yields 0.
    /// </summary>
    public static LinkMetrics Calculate(IEnumerable<Uri> links, string startingHost)
    {
        var total = 0;
        var internalCount = 0;

        foreach (var link in links)
        {
            if (!UrlNormalizer.IsSupportedScheme(link))
            {
                continue;
            }

            total++;
            if (IsInternal(link, startingHost))
            {
                internalCount++;
            }
        }

        var ratio = total == 0 ? 0d : (double)internalCount / total;
        return new LinkMetrics(total, internalCount, ratio);
    }

    /// <summary>Exact host match; subdomains are distinct hosts and the port is irrelevant.</summary>
    public static bool IsInternal(Uri link, string startingHost) =>
        string.Equals(link.Host, startingHost, StringComparison.OrdinalIgnoreCase);
}
