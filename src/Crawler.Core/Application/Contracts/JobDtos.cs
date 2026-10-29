using Crawler.Core.Domain;

namespace Crawler.Core.Application.Contracts;

// Frozen API contract — see docs/contracts.md. Changes require a decision-log entry.

public sealed record CreateJobRequest(string? Url, int? MaxDepth, int? MaxPages);

public sealed record CreateJobResponse(Guid JobId);

public sealed record JobDetailsDto(
    Guid JobId,
    string InputUrl,
    string NormalizedStartUrl,
    string StartingHost,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    int MaxDepth,
    int MaxPages,
    int DiscoveredPages,
    int ProcessedPages,
    int FailedPages,
    bool Truncated,
    bool CancellationRequested,
    int ProgressPercent);

public sealed record JobSummaryDto(
    Guid JobId,
    string InputUrl,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int MaxDepth,
    int DiscoveredPages,
    int ProcessedPages,
    int FailedPages,
    bool Truncated);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record JobTreeDto(Guid JobId, JobStatus Status, bool Truncated, int TotalNodes, TreeNodeDto? Root);

public sealed record TreeNodeDto(
    Guid PageId,
    string Url,
    int Depth,
    PageFetchStatus FetchStatus,
    int? HttpStatusCode,
    double DomainLinkRatio,
    int OutgoingLinkCount,
    int InternalLinkCount,
    string? ErrorMessage,
    IReadOnlyList<TreeNodeDto> Children);

/// <summary>One outgoing edge of a page. Target status is known only when the target became a page of the job.</summary>
public sealed record PageLinkDto(
    string TargetUrl,
    int OccurrenceCount,
    bool IsInternal,
    Guid? TargetPageId,
    PageFetchStatus? TargetFetchStatus);

public sealed record CancelJobResponse(Guid JobId, JobStatus Status, bool CancellationRequested);
