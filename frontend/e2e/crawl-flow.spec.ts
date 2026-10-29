import { expect, test } from '@playwright/test';
import { FIXTURE_SITE } from './support/env';
import { jobStatus, startCrawlViaUi } from './support/helpers';

test.describe('start crawl form validation', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('an empty URL is rejected before submitting', async ({ page }) => {
    await page.getByRole('button', { name: 'Start crawl' }).click();
    const url = page.getByLabel('Start URL');
    await expect(url).toHaveAttribute('aria-invalid', 'true');
    await expect(url).toHaveAccessibleDescription('Enter the URL to start crawling from.');
    await expect(page).toHaveURL(/\/$/);
  });

  test('a non-http URL is rejected before submitting', async ({ page }) => {
    await page.getByLabel('Start URL').fill('ftp://x');
    await page.getByRole('button', { name: 'Start crawl' }).click();
    await expect(page.getByLabel('Start URL')).toHaveAccessibleDescription(
      'Enter an absolute URL starting with http:// or https://.',
    );
    await expect(page).toHaveURL(/\/$/);
  });

  test('a loopback URL is rejected by the server and shown on the URL field', async ({ page }) => {
    await page.getByLabel('Start URL').fill('http://127.0.0.1/');
    const response = page.waitForResponse((r) => r.url().endsWith('/api/jobs') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Start crawl' }).click();
    expect((await response).status()).toBe(400);

    const url = page.getByLabel('Start URL');
    await expect(url).toHaveAttribute('aria-invalid', 'true');
    await expect(url).toHaveAccessibleDescription(/must not be a loopback, private or link-local address/);
    await expect(page).toHaveURL(/\/$/);
  });
});

test('crawling the fixture site shows progress, failures and the page tree', async ({ page }) => {
  await startCrawlViaUi(page, FIXTURE_SITE, 2);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(FIXTURE_SITE);
  await expect(jobStatus(page)).toHaveText('Job status: Completed', { timeout: 60_000 });

  const progress = page.locator('section', { has: page.getByRole('heading', { name: 'Progress' }) });
  await expect(progress.getByText('5 of 5 pages')).toBeVisible();
  const failedCounter = progress
    .getByRole('term')
    .getByText('Failed', { exact: true })
    .locator('xpath=following-sibling::dd[1]');
  await expect(failedCounter).toHaveText('1');
  await expect(page.getByText('1 page(s) could not be fetched')).toBeVisible();

  const rows = page.getByRole('list', { name: 'Crawled pages' }).getByRole('listitem');
  await expect(rows.first()).toContainText('75.0%');

  await page.getByRole('button', { name: 'Expand all' }).click();
  await expect(page.getByText('Showing 5 of 5 pages')).toBeVisible();
  await expect(rows.filter({ hasText: '/a.html' })).toHaveCount(1);
  await expect(rows.filter({ hasText: '/b.html' })).toHaveCount(1);
  const failedRow = rows.filter({ hasText: '/d.html' });
  await expect(failedRow).toContainText('Failed');
  await expect(failedRow).toContainText('404');

  // Outgoing links of the start page: /a.html is linked twice (fragment duplicate), the external site once.
  const rootRow = rows.first();
  const linksToggle = rootRow.getByRole('button', { name: /^Links \(\d+\)$/ });
  await linksToggle.click();
  await expect(linksToggle).toHaveAttribute('aria-expanded', 'true');
  const links = rootRow.getByRole('list', { name: /^Outgoing links of / }).getByRole('listitem');
  const aLink = links.filter({ hasText: '/a.html' });
  await expect(aLink).toHaveCount(1);
  await expect(aLink).toContainText('×2');
  await expect(aLink).toContainText('Internal');
  const externalLink = links.filter({ hasText: 'external.example.org' });
  await expect(externalLink).toHaveCount(1);
  await expect(externalLink).toContainText('External');
});
