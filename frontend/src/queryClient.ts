import { QueryClient } from '@tanstack/react-query';
import { shouldRetry } from './api/queries';
import { QUERY_RETRY_DELAY_MS } from './config';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      // A fixed, short retry delay: TanStack's default exponential delay kept an unreachable API on a spinner for >30 s.
      queries: { retry: shouldRetry, retryDelay: QUERY_RETRY_DELAY_MS, staleTime: 0, refetchOnWindowFocus: true },
      mutations: { retry: false },
    },
  });
}
