import { defineConfig, devices } from '@playwright/test';
import { BASE_URL } from './e2e/support/env';

const CI = !!process.env.CI;

/** Browser tests against an already running stack (`docker compose up`); see e2e/support/env.ts for targets. */
export default defineConfig({
  testDir: './e2e',
  // All specs share one stack (one database, one crawl worker). Running them one at a time keeps the
  // "newest first" history assertion and crawl timings deterministic.
  workers: 1,
  fullyParallel: false,
  forbidOnly: CI,
  retries: CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],
  outputDir: 'test-results',
  use: {
    baseURL: BASE_URL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
