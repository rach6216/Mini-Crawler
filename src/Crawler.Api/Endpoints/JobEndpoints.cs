using Crawler.Api.Services;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Crawler.Api.Endpoints;

public static class JobEndpoints
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/api/jobs").WithTags("Jobs");

        jobs.MapPost("/", CreateAsync);
        jobs.MapGet("/", ListAsync);
        jobs.MapGet("/{jobId:guid}", GetAsync);
        jobs.MapGet("/{jobId:guid}/tree", GetTreeAsync);
        jobs.MapPost("/{jobId:guid}/cancel", CancelAsync);

        return routes;
    }

    private static async Task<Results<Accepted<CreateJobResponse>, ValidationProblem>> CreateAsync(
        CreateJobRequest request,
        CreateJobValidator validator,
        JobCommandService commands,
        CancellationToken cancellationToken)
    {
        if (!validator.TryValidate(request, out var validated, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var jobId = await commands.CreateAsync(validated!, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{jobId}", new CreateJobResponse(jobId));
    }

    private static async Task<Results<Ok<PagedResult<JobSummaryDto>>, ValidationProblem>> ListAsync(
        JobQueryService queries,
        CancellationToken cancellationToken,
        int? page = null,
        int? pageSize = null)
    {
        var effectivePage = page ?? DefaultPage;
        var effectivePageSize = pageSize ?? DefaultPageSize;

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (effectivePage < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (effectivePageSize < 1 || effectivePageSize > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await queries.ListAsync(effectivePage, effectivePageSize, cancellationToken));
    }

    private static async Task<Results<Ok<JobDetailsDto>, ProblemHttpResult>> GetAsync(
        Guid jobId, JobQueryService queries, CancellationToken cancellationToken)
    {
        var job = await queries.GetDetailsAsync(jobId, cancellationToken);
        return job is null ? JobNotFound(jobId) : TypedResults.Ok(job);
    }

    private static async Task<Results<Ok<JobTreeDto>, ProblemHttpResult>> GetTreeAsync(
        Guid jobId, JobQueryService queries, CancellationToken cancellationToken)
    {
        var tree = await queries.GetTreeAsync(jobId, cancellationToken);
        return tree is null ? JobNotFound(jobId) : TypedResults.Ok(tree);
    }

    private static async Task<Results<Accepted<CancelJobResponse>, Ok<CancelJobResponse>, ProblemHttpResult>> CancelAsync(
        Guid jobId, JobCommandService commands, JobQueryService queries, CancellationToken cancellationToken)
    {
        var outcome = await commands.RequestCancellationAsync(jobId, cancellationToken);
        if (outcome == CancellationRequestOutcome.NotFound)
        {
            return JobNotFound(jobId);
        }

        var state = await queries.GetCancellationStateAsync(jobId, cancellationToken);
        if (state is null)
        {
            return JobNotFound(jobId);
        }

        return outcome switch
        {
            CancellationRequestOutcome.Canceled
                or CancellationRequestOutcome.Requested
                or CancellationRequestOutcome.AlreadyRequested => TypedResults.Accepted($"/api/jobs/{jobId}", state),
            CancellationRequestOutcome.AlreadyCanceled => TypedResults.Ok(state),
            CancellationRequestOutcome.AlreadyTerminal => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job can no longer be canceled",
                detail: $"Job is already {state.Status}."),
            _ => throw new InvalidOperationException($"Unexpected cancellation outcome {outcome}."),
        };
    }

    private static ProblemHttpResult JobNotFound(Guid jobId) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Job not found",
            detail: $"Job {jobId} does not exist.");
}
