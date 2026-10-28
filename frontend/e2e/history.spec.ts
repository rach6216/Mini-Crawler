import { expect, test } from '@playwright/test';
import { FIXTURE_SITE } from './support/env';
import { createJobViaApi, waitForTerminalViaApi } from './support/helpers';

test.describe.configure({ mode: 'serial' });

let jobId: string;

test.beforeAll(async ({ request }) => {
  jobId = await createJobViaApi(request, FIXTURE_SITE, 2);
  await waitForTerminalViaApi(request, jobId);
});

test.beforeEach(async ({ page }) => {
  await page.goto('/history');
});

test('lists the newest job first and opens its details on row click', async ({ page }) => {
  const firstRow = page.getByRole('table').getByRole('row').nth(1); // row 0 is the header
  await expect(firstRow.getByRole('link', { name: FIXTURE_SITE })).toHaveAttribute('href', `/jobs/${jobId}`);
  await expect(firstRow).toContainText('Completed');

  // Click a non-link cell to exercise the row handler rather than the URL link.
  await firstRow.getByRole('cell').last().click();
  await expect(page).toHaveURL(new RegExp(`/jobs/${jobId}$`));
  await expect(page.getByText(jobId, { exact: true })).toBeVisible();
});

test('shows pagination with Previous disabled on page 1', async ({ page }) => {
  const pagination = page.getByRole('navigation', { name: 'Pagination' });
  await expect(pagination.getByRole('button', { name: 'Previous' })).toBeDisabled();
  await expect(pagination.getByRole('button', { name: 'Next' })).toBeVisible();
  await expect(pagination.getByText(/^Page 1 of \d+$/)).toBeVisible();
});
