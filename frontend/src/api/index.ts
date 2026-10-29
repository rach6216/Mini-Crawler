import { API_BASE_URL, USE_MOCKS } from '../config';
import { createHttpClient } from './client';
import { createMockClient } from './mockClient';
import type { CrawlerApi } from './types';

export const usingMocks = USE_MOCKS;

export const api: CrawlerApi = usingMocks ? createMockClient() : createHttpClient(API_BASE_URL);
