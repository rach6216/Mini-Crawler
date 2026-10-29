using Crawler.Core.Application.Contracts;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Services;

public sealed class JobQueryService(CrawlerDbContext db)
{
    public Task<JobDetailsDto?> GetDetailsAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            // JobProgress.Percent runs client-side in the final projection; only the columns it needs are read.
            .Select(j => new JobDetailsDto(
                j.Id, j.InputUrl, j.NormalizedStartUrl, j.StartingHost, j.Status,
                j.CreatedAt, j.StartedAt, j.CompletedAt, j.FailureReason,
                j.MaxDepth, j.MaxPages, j.DiscoveredPages, j.ProcessedPages, j.FailedPages,
                j.Truncated, j.CancellationRequested,
                JobProgress.Percent(j.Status, j.ProcessedPages, j.DiscoveredPages)))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<JobSummaryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var totalCount = await db.Jobs.AsNoTracking().CountAsync(cancellationToken);
        var items = await db.Jobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new JobSummaryDto(
                j.Id, j.InputUrl, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt,
                j.MaxDepth, j.DiscoveredPages, j.ProcessedPages, j.FailedPages, j.Truncated))
            .ToListAsync(cancellationToken);

        return new PagedResult<JobSummaryDto>(items, page, pageSize, totalCount);
    }

    public async Task<JobTreeDto?> GetTreeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Status, j.Truncated })
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.JobId == jobId)
            .OrderBy(p => p.DiscoveryOrder)
            .ToListAsync(cancellationToken);

        return new JobTreeDto(jobId, job.Status, job.Truncated, pages.Count, TreeBuilder.Build(pages));
    }

    /// <returns>Null when the job does not exist or the page does not belong to it.</returns>
    public async Task<PagedResult<PageLinkDto>?> GetPageLinksAsync(
        Guid jobId, Guid pageId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var pageExists = await db.Pages.AsNoTracking().AnyAsync(p => p.Id == pageId && p.JobId == jobId, cancellationToken);
        if (!pageExists)
        {
            return null;
        }

        // Served by ux_crawl_edges_job_source_target (job_id, source_page_id prefix).
        var edges = db.Edges.AsNoTracking().Where(e => e.JobId == jobId && e.SourcePageId == pageId);
        var totalCount = await edges.CountAsync(cancellationToken);
        var items = await (
                from edge in edges
                join target in db.Pages.AsNoTracking() on edge.TargetPageId equals (Guid?)target.Id into targets
                from target in targets.DefaultIfEmpty()
                orderby edge.Id
                select new PageLinkDto(
                    edge.TargetNormalizedUrl,
                    edge.OccurrenceCount,
                    edge.IsInternal,
                    edge.TargetPageId,
                    target == null ? null : target.FetchStatus))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PageLinkDto>(items, page, pageSize, totalCount);
    }

    public Task<CancelJobResponse?> GetCancellationStateAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new CancelJobResponse(j.Id, j.Status, j.CancellationRequested))
            .SingleOrDefaultAsync(cancellationToken);
}
