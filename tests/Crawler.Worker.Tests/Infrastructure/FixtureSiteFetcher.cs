using System.Collections.Concurrent;
using Crawler.Core.Application.Abstractions;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>
/// Serves <c>tests/fixtures/site</c> for any host without network access: <c>/</c> maps to index.html,
/// missing files are 404, non-HTML files are skipped as the real fetcher would (body never read).
/// </summary>
public sealed class FixtureSiteFetcher : IPageFetcher
{
    private readonly ConcurrentQueue<Uri> _calls = new();

    public static string SiteRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "site");

    public IReadOnlyList<Uri> Calls => [.. _calls];

    /// <summary>Invoked with the 1-based call number before the result is produced; may throw to simulate a crash.</summary>
    public Func<Uri, int, Task>? OnFetch { get; set; }

    public async Task<PageFetchResult> FetchAsync(Uri url, string startingHost, CancellationToken cancellationToken)
    {
        // Yield first so that failures surface through the task, like a real network call.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        _calls.Enqueue(url);
        if (OnFetch is not null)
        {
            await OnFetch(url, _calls.Count);
        }

        var relative = url.AbsolutePath == "/" ? "index.html" : url.AbsolutePath.TrimStart('/');
        var path = Path.Combine(SiteRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return PageFetchResult.Fail(404, url, "HTTP 404 Not Found");
        }

        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return PageFetchResult.Skip(200, url, "Unsupported content type 'image/png'");
        }

        return PageFetchResult.Ok(200, await File.ReadAllTextAsync(path, cancellationToken), url);
    }
}
