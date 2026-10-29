import { ApiError, NetworkError } from './errors';
import type {
  CancelJobResponse,
  CrawlerApi,
  CreateJobRequest,
  CreateJobResponse,
  JobDetailsDto,
  JobSummaryDto,
  JobTreeDto,
  PageLinkDto,
  PagedResult,
  ProblemDetails,
} from './types';

export function createHttpClient(baseUrl: string): CrawlerApi {
  const root = `${baseUrl.replace(/\/+$/, '')}/api`;

  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    let response: Response;
    try {
      response = await fetch(`${root}${path}`, {
        ...init,
        headers: {
          Accept: 'application/json',
          ...(init.body ? { 'Content-Type': 'application/json' } : {}),
          ...init.headers,
        },
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') throw error;
      throw new NetworkError(error);
    }

    if (!response.ok) throw new ApiError(response.status, await readProblem(response));
    return (await response.json()) as T;
  }

  return {
    createJob: (body: CreateJobRequest, signal) =>
      request<CreateJobResponse>('/jobs', { method: 'POST', body: JSON.stringify(body), signal }),
    getJob: (jobId, signal) => request<JobDetailsDto>(`/jobs/${encodeURIComponent(jobId)}`, { signal }),
    getJobTree: (jobId, signal) => request<JobTreeDto>(`/jobs/${encodeURIComponent(jobId)}/tree`, { signal }),
    getPageLinks: (jobId, pageId, page, pageSize, signal) =>
      request<PagedResult<PageLinkDto>>(
        `/jobs/${encodeURIComponent(jobId)}/pages/${encodeURIComponent(pageId)}/links?page=${page}&pageSize=${pageSize}`,
        { signal },
      ),
    listJobs: (page, pageSize, signal) =>
      request<PagedResult<JobSummaryDto>>(`/jobs?page=${page}&pageSize=${pageSize}`, { signal }),
    cancelJob: (jobId, signal) =>
      request<CancelJobResponse>(`/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST', signal }),
  };
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) return {};
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return {};
  }
}
