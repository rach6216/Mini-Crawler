using Crawler.Core.Application.Abstractions;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Outbox;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Services;

public sealed class JobCommandService(
    CrawlerDbContext db,
    IJobStateStore stateStore,
    OutboxSignal outboxSignal,
    TimeProvider clock,
    ILogger<JobCommandService> logger)
{
    public async Task<Guid> CreateAsync(ValidatedCreateJob request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var job = CrawlJob.Create(request.InputUrl, request.NormalizedStartUrl, request.MaxDepth, request.MaxPages, now);

        // Job and outbox row commit atomically (SaveChanges wraps both inserts in one transaction), so a
        // job can never exist without the message that will start it.
        db.Jobs.Add(job);
        db.OutboxMessages.Add(CrawlJobOutbox.CreateRequested(job, now));
        await db.SaveChangesAsync(cancellationToken);

        outboxSignal.Notify();

        using (logger.BeginScope("JobId:{JobId} CorrelationId:{CorrelationId}", job.Id, job.CorrelationId))
        {
            logger.LogInformation("Job {JobId} created for {StartUrl} (maxDepth {MaxDepth}, maxPages {MaxPages})",
                job.Id, job.NormalizedStartUrl, job.MaxDepth, job.MaxPages);
        }

        return job.Id;
    }

    public async Task<CancellationRequestOutcome> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var correlationId = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => (Guid?)j.CorrelationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (correlationId is null)
        {
            return CancellationRequestOutcome.NotFound;
        }

        using var scope = logger.BeginScope("JobId:{JobId} CorrelationId:{CorrelationId}", jobId, correlationId.Value);

        var outcome = await stateStore.RequestCancellationAsync(jobId, cancellationToken);
        logger.LogInformation("Cancellation of job {JobId} requested: {CancellationOutcome}", jobId, outcome);
        return outcome;
    }
}
