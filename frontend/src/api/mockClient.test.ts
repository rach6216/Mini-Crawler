import { describe, expect, it } from 'vitest';
import { ApiError } from './errors';
import { createMockClient } from './mockClient';
import { countNodes } from '../lib/tree';

function setup() {
  let t = Date.parse('2026-10-28T18:00:00.000Z');
  const clock = { advance: (ms: number) => (t += ms) };
  const api = createMockClient(() => t, 0);
  return { api, clock };
}

describe('mock client', () => {
  it('moves a job Pending -> Running -> Completed with consistent counters', async () => {
    const { api, clock } = setup();
    const { jobId } = await api.createJob({ url: 'https://site.test/', maxDepth: 2, maxPages: 200 });

    const pending = await api.getJob(jobId);
    expect(pending).toMatchObject({ status: 'Pending', discoveredPages: 0, progressPercent: 0 });
    expect((await api.getJobTree(jobId)).root).toBeNull();

    clock.advance(4_000);
    const running = await api.getJob(jobId);
    expect(running.status).toBe('Running');
    expect(running.processedPages).toBeLessThanOrEqual(running.discoveredPages);
    const runningTree = await api.getJobTree(jobId);
    expect(countNodes(runningTree.root)).toBe(running.discoveredPages);

    clock.advance(10_000);
    const done = await api.getJob(jobId);
    expect(done).toMatchObject({ status: 'Completed', progressPercent: 100 });
    expect(done.processedPages).toBe(done.discoveredPages);
    const tree = await api.getJobTree(jobId);
    expect(tree.totalNodes).toBe(done.discoveredPages);
    expect(countNodes(tree.root)).toBe(done.discoveredPages);
  });

  it('cancels a running job after the worker observes the flag, and rejects canceling finished jobs', async () => {
    const { api, clock } = setup();
    const { jobId } = await api.createJob({ url: 'https://site.test/' });
    clock.advance(2_000);
    const response = await api.cancelJob(jobId);
    expect(response).toMatchObject({ status: 'Running', cancellationRequested: true });
    clock.advance(2_000);
    expect((await api.getJob(jobId)).status).toBe('Canceled');
    expect((await api.cancelJob(jobId)).status).toBe('Canceled');

    const { jobId: other } = await api.createJob({ url: 'https://site.test/' });
    clock.advance(20_000);
    await expect(api.cancelJob(other)).rejects.toMatchObject({ status: 409 });
  });

  it('returns validation problems keyed by field', async () => {
    const { api } = setup();
    const error = await api.createJob({ url: 'ftp://x', maxDepth: 9 }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.isValidation).toBe(true);
    expect(apiError.fieldErrors('url')).toHaveLength(1);
    expect(apiError.fieldErrors('maxDepth')).toHaveLength(1);
    expect(apiError.fieldErrors('maxPages')).toHaveLength(0);
  });

  it('marks jobs truncated when maxPages is hit and pages history newest first', async () => {
    const { api } = setup();
    const first = await api.listJobs(1, 20);
    expect(first.items).toHaveLength(20);
    expect(first.totalCount).toBeGreaterThan(20);
    const created = first.items.map((j) => Date.parse(j.createdAt));
    expect(created).toEqual([...created].sort((a, b) => b - a));
    expect(first.items.some((j) => j.truncated)).toBe(true);
    await expect(api.getJob('missing')).rejects.toMatchObject({ status: 404 });
  });

  it('serves page links consistent with the tree counters', async () => {
    const { api, clock } = setup();
    const { jobId } = await api.createJob({ url: 'https://site.test/', maxDepth: 2, maxPages: 200 });
    clock.advance(20_000);
    const root = (await api.getJobTree(jobId)).root!;

    const links = await api.getPageLinks(jobId, root.pageId, 1, 500);
    const occurrences = (internal: boolean) =>
      links.items.filter((l) => l.isInternal === internal).reduce((sum, l) => sum + l.occurrenceCount, 0);
    expect(occurrences(true)).toBe(root.internalLinkCount);
    expect(occurrences(false)).toBe(root.outgoingLinkCount - root.internalLinkCount);
    expect(links.totalCount).toBe(links.items.length);
    const childIds = new Set(root.children.map((c) => c.pageId));
    expect(links.items.filter((l) => l.targetPageId !== null).every((l) => childIds.has(l.targetPageId!))).toBe(true);

    const firstPage = await api.getPageLinks(jobId, root.pageId, 1, 1);
    expect(firstPage.items).toEqual(links.items.slice(0, 1));
    await expect(api.getPageLinks(jobId, 'missing', 1, 100)).rejects.toMatchObject({ status: 404 });
  });
});
