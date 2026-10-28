import { QueryClient } from '@tanstack/react-query';
import { shouldRetry } from './api/queries';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: shouldRetry, staleTime: 0, refetchOnWindowFocus: true },
      mutations: { retry: false },
    },
  });
}
