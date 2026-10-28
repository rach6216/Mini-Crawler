import { expect, test } from '@playwright/test';
import { FIXTURE_SITE } from './support/env';
import { createJobViaApi, jobStatus, waitForTerminalViaApi } from './support/helpers';

const PHONE = { width: 400, height: 900 };

test.use({ viewport: PHONE });

test('job details of a completed crawl fit a 400px viewport without horizontal overflow', async ({ page, request }) => {
  const jobId = await createJobViaApi(request, FIXTURE_SITE, 2);
  await waitForTerminalViaApi(request, jobId);

  await page.goto(`/jobs/${jobId}`);
  await expect(jobStatus(page)).toHaveText('Job status: Completed');
  await page.getByRole('button', { name: 'Expand all' }).click();
  await expect(page.getByText('Showing 5 of 5 pages')).toBeVisible();

  const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
  expect(scrollWidth).toBeLessThanOrEqual(PHONE.width + 1);
});
