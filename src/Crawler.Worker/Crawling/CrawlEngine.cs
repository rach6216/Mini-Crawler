using System.Net;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Crawling;

public interface ICrawlEngine
{
    /// <summary>Runs (or resumes) the crawl of a job this worker has claimed.</summary>
    /// <exception cref="LeaseLostException">Another owner took over; nothing further was written.</exception>
    Task<CrawlOutcome> RunAsync(CrawlJob job, string owner, CancellationToken cancellationToken);
}

/// <summary>
/// Level-synchronous BFS resumed from the durable frontier. Fetches within a level run
/// concurrently through a sliding window, but results are persisted strictly in page order so
/// discovery order and parent assignment are deterministic. Each page is persisted with its children,
/// edges and counter delta in one transaction guarded by lease ownership. robots.txt is evaluated once per
/// run; a disallowed page keeps its slot and is persisted as Skipped without being fetched.
/// </summary>
public sealed class CrawlEngine(
    CrawlerDbContext db,
    IServiceScopeFactory scopeFactory,
    IJobStateStore stateStore,
    IPageFetcher fetcher,
    IRobotsPolicyProvider robotsPolicy,
    ILinkExtractor linkExtractor,
    IOptions<CrawlerOptions> options,
    TimeProvider clock,
    ILogger<CrawlEngine> logger) : ICrawlEngine
{
    public async Task<CrawlOutcome> RunAsync(CrawlJob job, string owner, CancellationToken cancellationToken)
    {
        var frontier = await LoadFrontierAsync(job, owner, cancellationToken);

        logger.LogInformation(
            "Crawl of job {JobId} started at {StartUrl} (maxDepth {MaxDepth}, maxPages {MaxPages}, {KnownPages} known pages, {PendingPages} pending)",
            job.Id, job.NormalizedStartUrl, job.MaxDepth, job.MaxPages, frontier.Count, frontier.PendingCount);

        // The heartbeat renews the lease on its own DbContext while pages are being fetched, so the lease can be
        // much shorter than the worst-case page time. Losing the lease cancels everything below.
        await using var heartbeat = LeaseHeartbeat.Start(scopeFactory, job.Id, owner, options.Value.Worker, clock, logger, cancellationToken);
        var token = heartbeat.Token;

        try
        {
            var robots = frontier.PendingCount > 0
                ? await robotsPolicy.GetAsync(new Uri(job.NormalizedStartUrl), token)
                : RobotsDecision.AllowAll;

            while (frontier.NextLevel() is { Count: > 0 } level)
            {
                logger.LogInformation("Job {JobId}: processing depth {Depth} with {LevelPages} pages", job.Id, level[0].Depth, level.Count);

                var outcome = await ProcessLevelAsync(job, owner, frontier, level, robots, token);
                if (outcome == CrawlOutcome.Canceled)
                {
                    return CrawlOutcome.Canceled;
                }
            }

            if (!await stateStore.TryCompleteAsync(job.Id, owner, token))
            {
                throw new LeaseLostException(job.Id, "completion");
            }
        }
        catch (OperationCanceledException) when (heartbeat.LeaseLost)
        {
            throw new LeaseLostException(job.Id, "lease renewal");
        }

        logger.LogInformation("Job {JobId} completed with {DiscoveredPages} pages", job.Id, frontier.Count);
        return CrawlOutcome.Completed;
    }

    private async Task<CrawlOutcome?> ProcessLevelAsync(
        CrawlJob job, string owner, Frontier frontier, IReadOnlyList<CrawlPage> level, RobotsDecision robots,
        CancellationToken cancellationToken)
    {
        var concurrency = options.Value.Http.Concurrency;
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var window = new Queue<Task<PageFetchResult>>(concurrency);
        var started = 0;

        try
        {
            for (var index = 0; index < level.Count; index++)
            {
                if (await stateStore.IsCancellationRequestedAsync(job.Id, cancellationToken))
                {
                    if (!await stateStore.TryCancelRunningAsync(job.Id, owner, cancellationToken))
                    {
                        throw new LeaseLostException(job.Id, "cancellation");
                    }

                    logger.LogInformation("Job {JobId} canceled after {ProcessedPages} processed pages in this run", job.Id, frontier.ProcessedInRun);
                    return CrawlOutcome.Canceled;
                }

                while (started < level.Count && started < index + concurrency)
                {
                    var url = new Uri(level[started].NormalizedUrl);
                    window.Enqueue(robots.IsAllowed(url)
                        ? fetcher.FetchAsync(url, job.StartingHost, windowCts.Token)
                        : Task.FromResult(PageFetchResult.Skip(null, url, robots.SkipReason)));
                    started++;
                }

                var result = await window.Dequeue();
                await PersistPageAsync(job, owner, frontier, level[index], result, cancellationToken);
            }

            return null;
        }
        finally
        {
            // Abandon look-ahead fetches when the level ends early (cancellation, lease loss, failure).
            await windowCts.CancelAsync();
            foreach (var pending in window)
            {
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Abandoned fetch for job {JobId} failed", job.Id);
                }
            }
        }
    }

    private async Task PersistPageAsync(
        CrawlJob job, string owner, Frontier frontier, CrawlPage page, PageFetchResult result, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        IReadOnlyList<Uri> links = [];
        switch (result.Outcome)
        {
            case PageFetchOutcome.Html:
                links = linkExtractor.Extract(result.Html ?? string.Empty, result.FinalUrl);
                page.MarkFetched(result.HttpStatusCode ?? (int)HttpStatusCode.OK, DomainLinkRatio.Calculate(links, job.StartingHost), now);
                break;
            case PageFetchOutcome.Skipped:
                page.MarkSkipped(result.HttpStatusCode, FitErrorColumn(result.Error ?? "Skipped"), now);
                break;
            default:
                page.MarkFailed(result.HttpStatusCode, FitErrorColumn(result.Error ?? "Failed"), now);
                break;
        }

        var children = new List<CrawlPage>();
        var edges = new List<CrawlEdge>();
        var truncatedNow = false;

        foreach (var (target, occurrences) in AggregateTargets(links))
        {
            var isInternal = DomainLinkRatio.IsInternal(target, job.StartingHost);
            var targetPage = frontier.Find(target.AbsoluteUri);

            if (targetPage is null && isInternal && page.Depth < job.MaxDepth)
            {
                if (frontier.Count < job.MaxPages)
                {
                    targetPage = frontier.AddChild(job.Id, target, page);
                    children.Add(targetPage);
                }
                else
                {
                    truncatedNow = true;
                }
            }

            edges.Add(new CrawlEdge
            {
                JobId = job.Id,
                SourcePageId = page.Id,
                TargetNormalizedUrl = target.AbsoluteUri,
                TargetPageId = targetPage?.Id,
                OccurrenceCount = occurrences,
                IsInternal = isInternal,
            });
        }

        var failed = page.FetchStatus == PageFetchStatus.Failed;
        var delta = new PageProgressDelta(children.Count, 1, failed ? 1 : 0, truncatedNow);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // The guarded counter update goes first so a lost lease is detected before any row is written.
            if (!await stateStore.TryApplyProgressAsync(job.Id, owner, delta, cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new LeaseLostException(job.Id, "page persistence");
            }

            db.Pages.Update(page);
            db.Pages.AddRange(children);
            db.Edges.AddRange(edges);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Keeps the change tracker (and memory) flat across up to MaxPages transactions.
        db.ChangeTracker.Clear();
        frontier.MarkProcessed();

        logger.LogDebug(
            "Job {JobId}: page {Url} (depth {Depth}) {FetchStatus} with {LinkCount} links, {NewPages} new pages",
            job.Id, page.NormalizedUrl, page.Depth, page.FetchStatus, links.Count, children.Count);

        if (truncatedNow && !frontier.TruncationLogged)
        {
            frontier.TruncationLogged = true;
            logger.LogInformation("Job {JobId} reached maxPages {MaxPages}; further in-scope pages are recorded as edges only", job.Id, job.MaxPages);
        }
    }

    private async Task<Frontier> LoadFrontierAsync(CrawlJob job, string owner, CancellationToken cancellationToken)
    {
        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.JobId == job.Id)
            .OrderBy(p => p.Depth).ThenBy(p => p.DiscoveryOrder)
            .ToListAsync(cancellationToken);

        if (pages.Count == 0)
        {
            var root = CrawlPage.CreateRoot(job.Id, new Uri(job.NormalizedStartUrl));
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await stateStore.TryApplyProgressAsync(job.Id, owner, new PageProgressDelta(1, 0, 0, false), cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new LeaseLostException(job.Id, "root creation");
            }

            db.Pages.Add(root);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            pages.Add(root);
        }

        return new Frontier(pages);
    }

    // Error texts embed exception messages of arbitrary length; an overlong one must not fail the page transaction.
    private static string FitErrorColumn(string error) =>
        error.Length <= CrawlerDbContext.ErrorTextMaxLength ? error : error[..CrawlerDbContext.ErrorTextMaxLength];

    /// <summary>Distinct targets in first-seen order with their occurrence counts.</summary>
    private static List<(Uri Target, int Occurrences)> AggregateTargets(IReadOnlyList<Uri> links)
    {
        var order = new List<(Uri Target, int Occurrences)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            var key = link.AbsoluteUri;
            if (index.TryGetValue(key, out var position))
            {
                order[position] = (order[position].Target, order[position].Occurrences + 1);
            }
            else
            {
                index[key] = order.Count;
                order.Add((link, 1));
            }
        }

        return order;
    }

    /// <summary>In-memory BFS state: every page row of the job keyed by normalized URL.</summary>
    private sealed class Frontier
    {
        private readonly Dictionary<string, CrawlPage> _pages;
        private int _nextDiscoveryOrder;

        public Frontier(IReadOnlyList<CrawlPage> pages)
        {
            _pages = pages.ToDictionary(p => p.NormalizedUrl, StringComparer.Ordinal);
            _nextDiscoveryOrder = pages.Max(p => p.DiscoveryOrder) + 1;
        }

        public int Count => _pages.Count;

        public int PendingCount => _pages.Values.Count(p => p.FetchStatus == PageFetchStatus.Discovered);

        public int ProcessedInRun { get; private set; }

        public bool TruncationLogged { get; set; }

        public CrawlPage? Find(string normalizedUrl) => _pages.GetValueOrDefault(normalizedUrl);

        /// <summary>The shallowest unprocessed depth, ordered by discovery. A resumed job may have pending pages on two depths.</summary>
        public IReadOnlyList<CrawlPage> NextLevel()
        {
            var pending = _pages.Values.Where(p => p.FetchStatus == PageFetchStatus.Discovered).ToList();
            if (pending.Count == 0)
            {
                return [];
            }

            var depth = pending.Min(p => p.Depth);
            return pending.Where(p => p.Depth == depth).OrderBy(p => p.DiscoveryOrder).ToList();
        }

        public CrawlPage AddChild(Guid jobId, Uri url, CrawlPage parent)
        {
            var child = CrawlPage.CreateChild(jobId, url, parent.Depth + 1, parent.Id, _nextDiscoveryOrder++);
            _pages.Add(child.NormalizedUrl, child);
            return child;
        }

        public void MarkProcessed() => ProcessedInRun++;
    }

    private sealed class LeaseHeartbeat : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;

        private LeaseHeartbeat(CancellationTokenSource cts, Func<CancellationToken, Task> loop)
        {
            _cts = cts;
            _loop = loop(cts.Token);
        }

        public CancellationToken Token => _cts.Token;
        public bool LeaseLost { get; private set; }

        public static LeaseHeartbeat Start(
            IServiceScopeFactory scopeFactory, Guid jobId, string owner, WorkerOptions options, TimeProvider clock,
            ILogger logger, CancellationToken outer)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            LeaseHeartbeat? self = null;
            self = new LeaseHeartbeat(cts, async token =>
            {
                var interval = TimeSpan.FromSeconds(options.LeaseRenewSeconds);
                var lease = TimeSpan.FromSeconds(options.LeaseSeconds);
                try
                {
                    using var timer = new PeriodicTimer(interval, clock);
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        try
                        {
                            await using var scope = scopeFactory.CreateAsyncScope();
                            var store = scope.ServiceProvider.GetRequiredService<IJobStateStore>();
                            if (!await store.RenewLeaseAsync(jobId, owner, lease, token))
                            {
                                logger.LogWarning("Job {JobId}: lease renewal rejected; another owner took over, aborting this run", jobId);
                                self!.LeaseLost = true;
                                await cts.CancelAsync();
                                return;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // A transient database error must not abort the crawl; if it persists the lease expires
                            // and the next guarded write fails with LeaseLostException.
                            logger.LogWarning(ex, "Job {JobId}: lease renewal failed; will retry on the next heartbeat", jobId);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            return self;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }
}
