// Mirrors docs/contracts.md section 1 exactly (camelCase, ISO-8601 UTC strings).

export const JOB_STATUSES = ['Pending', 'Running', 'Completed', 'Failed', 'Canceled'] as const;
export type JobStatus = (typeof JOB_STATUSES)[number];

export const FETCH_STATUSES = ['Discovered', 'Fetched', 'Failed', 'Skipped'] as const;
export type FetchStatus = (typeof FETCH_STATUSES)[number];

export const TERMINAL_STATUSES: ReadonlySet<JobStatus> = new Set(['Completed', 'Failed', 'Canceled']);

export function isTerminal(status: JobStatus): boolean {
  return TERMINAL_STATUSES.has(status);
}

export interface CreateJobRequest {
  url: string;
  maxDepth?: number;
  maxPages?: number;
}

export interface CreateJobResponse {
  jobId: string;
}

export interface JobDetailsDto {
  jobId: string;
  inputUrl: string;
  normalizedStartUrl: string;
  startingHost: string;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
  maxDepth: number;
  maxPages: number;
  discoveredPages: number;
  processedPages: number;
  failedPages: number;
  truncated: boolean;
  cancellationRequested: boolean;
  progressPercent: number;
}

export interface TreeNodeDto {
  pageId: string;
  url: string;
  depth: number;
  fetchStatus: FetchStatus;
  httpStatusCode: number | null;
  domainLinkRatio: number;
  outgoingLinkCount: number;
  internalLinkCount: number;
  errorMessage: string | null;
  children: TreeNodeDto[];
}

export interface JobTreeDto {
  jobId: string;
  status: JobStatus;
  truncated: boolean;
  totalNodes: number;
  root: TreeNodeDto | null;
}

export interface JobSummaryDto {
  jobId: string;
  inputUrl: string;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  maxDepth: number;
  discoveredPages: number;
  processedPages: number;
  failedPages: number;
  truncated: boolean;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface CancelJobResponse {
  jobId: string;
  status: JobStatus;
  cancellationRequested: boolean;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
}

export interface CrawlerApi {
  createJob(request: CreateJobRequest, signal?: AbortSignal): Promise<CreateJobResponse>;
  getJob(jobId: string, signal?: AbortSignal): Promise<JobDetailsDto>;
  getJobTree(jobId: string, signal?: AbortSignal): Promise<JobTreeDto>;
  listJobs(page: number, pageSize: number, signal?: AbortSignal): Promise<PagedResult<JobSummaryDto>>;
  cancelJob(jobId: string, signal?: AbortSignal): Promise<CancelJobResponse>;
}
