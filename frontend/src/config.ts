/**
 * Every tunable of the UI, read once from Vite build-time env vars (`import.meta.env`) with defaults.
 * Set them in the shell or a `.env.local` file before `npm run dev` / `npm run build`:
 *
 *   VITE_API_BASE_URL           Origin of the API; empty = same origin (nginx / Vite dev proxy). Default ''.
 *   VITE_USE_MOCKS              'true' serves the in-memory mock API instead of HTTP. Default false.
 *   VITE_JOB_POLL_INTERVAL_MS   Poll interval for a running job and its tree. Default 2000.
 *   VITE_QUERY_RETRY_COUNT      Retries of a failed query (network / 5xx; 4xx is never retried). Default 1.
 *   VITE_QUERY_RETRY_DELAY_MS   Fixed pause before each retry. Default 1000.
 *   VITE_HISTORY_PAGE_SIZE      Jobs per history page (the API accepts 1–100). Default 20.
 *   VITE_TREE_DEFAULT_EXPANDED_DEPTH  Tree levels expanded on first render. Default 1.
 *   VITE_MAX_DEPTH_LIMIT / VITE_DEFAULT_MAX_DEPTH  Form bounds; mirror Crawler:Limits:MaxAllowedDepth / DefaultMaxDepth. Defaults 5 / 2.
 *   VITE_MAX_PAGES_LIMIT / VITE_DEFAULT_MAX_PAGES  Form bounds; mirror Crawler:Limits:MaxAllowedPages / DefaultMaxPages. Defaults 1000 / 200.
 *
 * An invalid value fails at startup instead of silently falling back, like the backend's ValidateOnStart.
 */

type IntEnvVar =
  | 'VITE_JOB_POLL_INTERVAL_MS'
  | 'VITE_QUERY_RETRY_COUNT'
  | 'VITE_QUERY_RETRY_DELAY_MS'
  | 'VITE_HISTORY_PAGE_SIZE'
  | 'VITE_PAGE_LINKS_PAGE_SIZE'
  | 'VITE_TREE_DEFAULT_EXPANDED_DEPTH'
  | 'VITE_MAX_DEPTH_LIMIT'
  | 'VITE_DEFAULT_MAX_DEPTH'
  | 'VITE_MAX_PAGES_LIMIT'
  | 'VITE_DEFAULT_MAX_PAGES';

function readInt(name: IntEnvVar, fallback: number, min: number, max: number): number {
  const raw = import.meta.env[name];
  if (raw === undefined || raw === '') return fallback;
  const value = Number(raw);
  if (!Number.isInteger(value) || value < min || value > max) {
    throw new Error(`${name} must be an integer from ${min} to ${max} (got '${raw}').`);
  }
  return value;
}

function requireAtMost(name: string, value: number, limitName: string, limit: number): void {
  if (value > limit) throw new Error(`${name} (${value}) must not exceed ${limitName} (${limit}).`);
}

export const API_BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? '';
export const USE_MOCKS: boolean = import.meta.env.VITE_USE_MOCKS === 'true';

export const JOB_POLL_INTERVAL_MS = readInt('VITE_JOB_POLL_INTERVAL_MS', 2_000, 250, 60_000);
export const QUERY_RETRY_COUNT = readInt('VITE_QUERY_RETRY_COUNT', 1, 0, 10);
export const QUERY_RETRY_DELAY_MS = readInt('VITE_QUERY_RETRY_DELAY_MS', 1_000, 0, 60_000);

/** The API caps pageSize at 100. */
export const HISTORY_PAGE_SIZE = readInt('VITE_HISTORY_PAGE_SIZE', 20, 1, 100);
export const PAGE_LINKS_PAGE_SIZE = readInt('VITE_PAGE_LINKS_PAGE_SIZE', 100, 1, 500);

export const TREE_DEFAULT_EXPANDED_DEPTH = readInt('VITE_TREE_DEFAULT_EXPANDED_DEPTH', 1, 0, 100);

/** Lower bounds are part of the API contract (depth 0 = start page only; at least one page); upper bounds match the backend option ranges. */
export const MIN_DEPTH = 0;
export const MIN_PAGES = 1;
export const MAX_DEPTH_LIMIT = readInt('VITE_MAX_DEPTH_LIMIT', 5, MIN_DEPTH, 10);
export const DEFAULT_MAX_DEPTH = readInt('VITE_DEFAULT_MAX_DEPTH', 2, MIN_DEPTH, 10);
export const MAX_PAGES_LIMIT = readInt('VITE_MAX_PAGES_LIMIT', 1_000, MIN_PAGES, 10_000);
export const DEFAULT_MAX_PAGES = readInt('VITE_DEFAULT_MAX_PAGES', 200, MIN_PAGES, 10_000);

requireAtMost('VITE_DEFAULT_MAX_DEPTH', DEFAULT_MAX_DEPTH, 'VITE_MAX_DEPTH_LIMIT', MAX_DEPTH_LIMIT);
requireAtMost('VITE_DEFAULT_MAX_PAGES', DEFAULT_MAX_PAGES, 'VITE_MAX_PAGES_LIMIT', MAX_PAGES_LIMIT);
