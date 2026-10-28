import { createHttpClient } from './client';
import { createMockClient } from './mockClient';
import type { CrawlerApi } from './types';

export const usingMocks = import.meta.env.VITE_USE_MOCKS === 'true';

const baseUrl = import.meta.env.VITE_API_BASE_URL ?? '';

export const api: CrawlerApi = usingMocks ? createMockClient() : createHttpClient(baseUrl);
