namespace Crawler.Core.Application.Abstractions;

public interface ILinkExtractor
{
    /// <summary>
    /// Returns every eligible outgoing link of the document as a normalized absolute http(s) URI, in
    /// document order and with duplicates preserved. Resolution honours an HTML <c>&lt;base href&gt;</c>
    /// when present, otherwise <paramref name="documentUrl"/>. Unsupported schemes and empty hrefs are dropped.
    /// </summary>
    IReadOnlyList<Uri> Extract(string html, Uri documentUrl);
}
