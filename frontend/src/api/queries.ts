import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './index';
import { ApiError } from './errors';
import { isTerminal, type CreateJobRequest, type JobDetailsDto } from './types';

export const POLL_INTERVAL_MS = 2_000;

export const jobKeys = {
  all: ['jobs'] as const,
  lists: () => [...jobKeys.all, 'list'] as const,
  list: (page: number, pageSize: number) => [...jobKeys.lists(), { page, pageSize }] as const,
  detail: (jobId: string) => [...jobKeys.all, 'detail', jobId] as const,
  tree: (jobId: string) => [...jobKeys.all, 'tree', jobId] as const,
};

/** 4xx responses are deterministic; retrying them only delays the error state. */
export function shouldRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false;
  return failureCount < 2;
}

export function useJob(jobId: string) {
  return useQuery({
    queryKey: jobKeys.detail(jobId),
    queryFn: ({ signal }) => api.getJob(jobId, signal),
    refetchInterval: (query) => {
      const data = query.state.data;
      // Keep polling through transient refetch errors; stop only if nothing was ever loaded (e.g. 404).
      if (query.state.status === 'error' && !data) return false;
      return data && isTerminal(data.status) ? false : POLL_INTERVAL_MS;
    },
  });
}

/** The tree endpoint has nothing to show until the start page row exists. */
export function isTreeAvailable(job: JobDetailsDto | undefined): boolean {
  return !!job && (job.discoveredPages > 0 || isTerminal(job.status));
}

export function useJobTree(jobId: string, job: JobDetailsDto | undefined) {
  const enabled = isTreeAvailable(job);
  return useQuery({
    queryKey: jobKeys.tree(jobId),
    queryFn: ({ signal }) => api.getJobTree(jobId, signal),
    enabled,
    // The tree carries its own status snapshot, so it stops polling exactly when the
    // last consistent tree has been received rather than when the job query noticed.
    refetchInterval: (query) => {
      const data = query.state.data;
      // Keep polling through transient refetch errors; stop only if nothing was ever loaded (e.g. 404).
      if (query.state.status === 'error' && !data) return false;
      return data && isTerminal(data.status) ? false : POLL_INTERVAL_MS;
    },
  });
}

export function useJobs(page: number, pageSize: number) {
  return useQuery({
    queryKey: jobKeys.list(page, pageSize),
    queryFn: ({ signal }) => api.listJobs(page, pageSize, signal),
    placeholderData: keepPreviousData,
  });
}

export function useCreateJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateJobRequest) => api.createJob(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: jobKeys.lists() }),
  });
}

export function useCancelJob(jobId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => api.cancelJob(jobId),
    onSuccess: (result) => {
      queryClient.setQueryData<JobDetailsDto>(jobKeys.detail(jobId), (current) =>
        current ? { ...current, status: result.status, cancellationRequested: result.cancellationRequested } : current,
      );
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: jobKeys.detail(jobId) }),
        queryClient.invalidateQueries({ queryKey: jobKeys.tree(jobId) }),
        queryClient.invalidateQueries({ queryKey: jobKeys.lists() }),
      ]),
  });
}
