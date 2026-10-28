using Crawler.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>Everything persisted for one job, read back in a deterministic order.</summary>
public sealed record CrawlSnapshot(CrawlJob Job, IReadOnlyList<CrawlPage> Pages, IReadOnlyList<CrawlEdge> Edges)
{
    public static async Task<CrawlSnapshot> LoadAsync(PostgresFixture postgres, Guid jobId)
    {
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var pages = await db.Pages.AsNoTracking().Where(p => p.JobId == jobId).OrderBy(p => p.DiscoveryOrder).ToListAsync();
        var edges = await db.Edges.AsNoTracking().Where(e => e.JobId == jobId).OrderBy(e => e.Id).ToListAsync();
        return new CrawlSnapshot(job, pages, edges);
    }

    public CrawlPage Page(string path) =>
        Pages.Single(p => p.NormalizedUrl == Url(path));

    public CrawlEdge Edge(string sourcePath, string targetUrl) =>
        Edges.Single(e => e.SourcePageId == Page(sourcePath).Id && e.TargetNormalizedUrl == ResolveTarget(targetUrl));

    public IReadOnlyList<CrawlEdge> EdgesFrom(string sourcePath) =>
        Edges.Where(e => e.SourcePageId == Page(sourcePath).Id).ToList();

    public static string Url(string path) => new Uri(new Uri(WorkerTestServices.StartUrl), path).AbsoluteUri;

    // On Linux a rooted path such as "/d.html" parses as an absolute file:// URI, so the scheme must be
    // checked explicitly; otherwise site-relative paths would never resolve against the start URL there.
    private static string ResolveTarget(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var absolute) && UrlNormalizer.IsSupportedScheme(absolute)
            ? absolute.AbsoluteUri
            : Url(target);
}
