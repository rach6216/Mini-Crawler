namespace Crawler.Core.Domain;

public enum PageFetchStatus
{
    /// <summary>Queued for crawling; a slot within maxPages has been assigned but the page has not been fetched yet.</summary>
    Discovered,
    Fetched,
    Failed,
    /// <summary>Deliberately not fetched to completion: non-HTML content, external redirect, or oversized body.</summary>
    Skipped,
}
