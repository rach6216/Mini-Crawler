import { expect, type APIRequestContext, type Page } from '@playwright/test';

const TERMINAL_STATUS = /^(Completed|Failed|Canceled)$/;
export const JOB_URL = /\/jobs\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

/** Submits the Start Crawl form and returns the new job id once the details page is open. */
export async function startCrawlViaUi(page: Page, url: string, maxDepth: number): Promise<string> {
  await page.goto('/');
  await page.getByLabel('Start URL').fill(url);
  await page.getByLabel('Max depth').fill(String(maxDepth));
  await page.getByRole('button', { name: 'Start crawl' }).click();
  await expect(page).toHaveURL(JOB_URL);
  const match = JOB_URL.exec(page.url());
  if (!match?.[1]) throw new Error(`No job id in ${page.url()}`);
  return match[1];
}

/** The visually hidden live region that announces the job status (the badge itself has no accessible name). */
export function jobStatus(page: Page) {
  return page.getByText(/^Job status: /);
}

/** Seeds a job through the API for specs that test other screens than Start Crawl. */
export async function createJobViaApi(request: APIRequestContext, url: string, maxDepth: number): Promise<string> {
  const response = await request.post('/api/jobs', { data: { url, maxDepth } });
  expect(response.status(), await response.text()).toBe(202);
  const body = (await response.json()) as { jobId: string };
  return body.jobId;
}

export async function waitForTerminalViaApi(request: APIRequestContext, jobId: string, timeout = 60_000): Promise<void> {
  await expect
    .poll(
      async () => {
        const response = await request.get(`/api/jobs/${jobId}`);
        return ((await response.json()) as { status: string }).status;
      },
      { message: `job ${jobId} reaches a terminal status`, timeout, intervals: [500] },
    )
    .toMatch(TERMINAL_STATUS);
}
