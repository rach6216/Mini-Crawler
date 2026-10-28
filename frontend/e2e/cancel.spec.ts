import { expect, test } from '@playwright/test';
import { SLOW_FIXTURE_SITE } from './support/env';
import { jobStatus, startCrawlViaUi } from './support/helpers';

test('a running crawl can be canceled from the details page', async ({ page }) => {
  await startCrawlViaUi(page, SLOW_FIXTURE_SITE, 5);
  // The slow fixture takes ~6 s per page, so the job is still running well after it starts.
  await expect(jobStatus(page)).toHaveText('Job status: Running', { timeout: 30_000 });

  await page.getByRole('button', { name: 'Cancel crawl' }).click();
  await expect(
    page.getByRole('button', { name: 'Cancellation requested…' }).or(page.getByText('Job status: Canceled')),
  ).toBeAttached({ timeout: 30_000 });

  await expect(jobStatus(page)).toHaveText('Job status: Canceled', { timeout: 30_000 });
  await expect(page.getByRole('button', { name: /Cancel/ })).toHaveCount(0);
});
