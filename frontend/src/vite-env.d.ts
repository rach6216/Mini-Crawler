/// <reference types="vite/client" />

// Documented in src/config.ts, the only module that reads them.
interface ImportMetaEnv {
  readonly VITE_USE_MOCKS?: string;
  readonly VITE_API_BASE_URL?: string;
  readonly VITE_JOB_POLL_INTERVAL_MS?: string;
  readonly VITE_QUERY_RETRY_COUNT?: string;
  readonly VITE_QUERY_RETRY_DELAY_MS?: string;
  readonly VITE_HISTORY_PAGE_SIZE?: string;
  readonly VITE_PAGE_LINKS_PAGE_SIZE?: string;
  readonly VITE_TREE_DEFAULT_EXPANDED_DEPTH?: string;
  readonly VITE_MAX_DEPTH_LIMIT?: string;
  readonly VITE_DEFAULT_MAX_DEPTH?: string;
  readonly VITE_MAX_PAGES_LIMIT?: string;
  readonly VITE_DEFAULT_MAX_PAGES?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
