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

    public Task<CancelJobResponse?> GetCancellationStateAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new CancelJobResponse(j.Id, j.Status, j.CancellationRequested))
            .SingleOrDefaultAsync(cancellationToken);
}
