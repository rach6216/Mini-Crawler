// In-memory stand-in for the crawler API, enabled with VITE_USE_MOCKS=true.
// Job state is derived from elapsed time so polling sees realistic transitions
// (Pending -> Running with growing counters -> terminal) without timers.
import { ApiError } from './errors';
import type {
  CancelJobResponse,
  CrawlerApi,
  CreateJobRequest,
  CreateJobResponse,
  FetchStatus,
  JobDetailsDto,
  JobStatus,
  JobSummaryDto,
  JobTreeDto,
  PagedResult,
  TreeNodeDto,
} from './types';

const LIMITS = { defaultMaxDepth: 2, maxAllowedDepth: 5, defaultMaxPages: 200, maxAllowedPages: 1000 } as const;
const PENDING_MS = 1_000;
const RUNNING_MS = 8_000;
const CANCEL_OBSERVED_AFTER_MS = 1_500;
const LATENCY_MS = 220;

const BRANCHING_NORMAL = [6, 4, 3, 2, 2] as const;
const BRANCHING_LARGE = [10, 8, 6, 4, 3] as const;
const SECTION_WORDS = [
  'about', 'blog', 'docs', 'pricing', 'careers', 'contact', 'guides', 'api',
  'changelog', 'team', 'press', 'legal', 'status', 'community', 'events', 'support',
] as const;

/**
 * normal: typical site. large: ~1000 pages for tree performance. failed: job-level failure.
 * rootFailed: job completes but the start page could not be fetched.
 * Hostnames containing "large", "broken" or "unreachable" pick the matching scenario on create.
 */
type Scenario = 'normal' | 'large' | 'failed' | 'rootFailed';

interface TemplateNode {
  pageId: string;
  parentIndex: number | null;
  url: string;
  depth: number;
  fetchStatus: Exclude<FetchStatus, 'Discovered'>;
  httpStatusCode: number | null;
  domainLinkRatio: number;
  outgoingLinkCount: number;
  internalLinkCount: number;
  errorMessage: string | null;
}

interface MockJob {
  jobId: string;
  inputUrl: string;
  normalizedStartUrl: string;
  startingHost: string;
  createdAtMs: number;
  maxDepth: number;
  maxPages: number;
  scenario: Scenario;
  /** BFS discovery order; every parent precedes its children, so any prefix is a valid tree. */
  nodes: TemplateNode[];
  truncatedTemplate: boolean;
  cancelRequestedAtMs: number | null;
}

interface Snapshot {
  status: JobStatus;
  startedAtMs: number | null;
  completedAtMs: number | null;
  failureReason: string | null;
  discovered: number;
  processed: number;
}

export function createMockClient(now: () => number = Date.now, latencyMs: number = LATENCY_MS): CrawlerApi {
  const jobs = new Map<string, MockJob>();
  for (const job of seedJobs(now())) jobs.set(job.jobId, job);

  const delay = () => new Promise<void>((resolve) => setTimeout(resolve, latencyMs));

  function find(jobId: string): MockJob {
    const job = jobs.get(jobId);
    if (!job) throw new ApiError(404, { title: 'Not Found', detail: `Job ${jobId} was not found.`, status: 404 });
    return job;
  }

  return {
    async createJob(request: CreateJobRequest): Promise<CreateJobResponse> {
      await delay();
      const errors = validate(request);
      if (Object.keys(errors).length > 0) {
        throw new ApiError(400, { title: 'One or more validation errors occurred.', status: 400, errors });
      }
      const host = new URL(request.url.trim()).hostname;
      const scenario: Scenario = host.includes('unreachable')
        ? 'rootFailed'
        : host.includes('broken')
          ? 'failed'
          : host.includes('large')
            ? 'large'
            : 'normal';
      const job = buildJob({
        jobId: crypto.randomUUID(),
        inputUrl: request.url.trim(),
        createdAtMs: now(),
        maxDepth: request.maxDepth ?? LIMITS.defaultMaxDepth,
        maxPages: request.maxPages ?? LIMITS.defaultMaxPages,
        scenario,
      });
      jobs.set(job.jobId, job);
      return { jobId: job.jobId };
    },

    async getJob(jobId: string): Promise<JobDetailsDto> {
      await delay();
      return toDetails(find(jobId), now());
    },

    async getJobTree(jobId: string): Promise<JobTreeDto> {
      await delay();
      const job = find(jobId);
      const snap = snapshot(job, now());
      const visible = job.nodes.slice(0, snap.discovered);
      return {
        jobId: job.jobId,
        status: snap.status,
        truncated: isTruncated(job, snap),
        totalNodes: visible.length,
        root: buildTree(visible, snap.processed),
      };
    },

    async listJobs(page: number, pageSize: number): Promise<PagedResult<JobSummaryDto>> {
      await delay();
      const t = now();
      const all = [...jobs.values()].sort((a, b) => b.createdAtMs - a.createdAtMs);
      const items = all.slice((page - 1) * pageSize, page * pageSize).map((job): JobSummaryDto => {
        const d = toDetails(job, t);
        return {
          jobId: d.jobId,
          inputUrl: d.inputUrl,
          status: d.status,
          createdAt: d.createdAt,
          startedAt: d.startedAt,
          completedAt: d.completedAt,
          maxDepth: d.maxDepth,
          discoveredPages: d.discoveredPages,
          processedPages: d.processedPages,
          failedPages: d.failedPages,
          truncated: d.truncated,
        };
      });
      return { items, page, pageSize, totalCount: all.length };
    },

    async cancelJob(jobId: string): Promise<CancelJobResponse> {
      await delay();
      const job = find(jobId);
      const t = now();
      const before = snapshot(job, t);
      if (before.status === 'Completed' || before.status === 'Failed') {
        throw new ApiError(409, {
          title: 'Conflict',
          status: 409,
          detail: `Job is already ${before.status} and cannot be canceled.`,
        });
      }
      if (before.status !== 'Canceled' && job.cancelRequestedAtMs === null) job.cancelRequestedAtMs = t;
      return { jobId, status: snapshot(job, t).status, cancellationRequested: true };
    },
  };
}

function validate(request: CreateJobRequest): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  let parsed: URL | null = null;
  try {
    parsed = new URL(request.url.trim());
  } catch {
    parsed = null;
  }
  if (!parsed || (parsed.protocol !== 'http:' && parsed.protocol !== 'https:')) {
    errors.Url = ['The URL must be an absolute http or https URL.'];
  } else if (/^(127\.|10\.|192\.168\.|169\.254\.)|^localhost$|^\[::1\]$/.test(parsed.hostname)) {
    errors.Url = ['The URL host must not be a loopback, private or link-local address.'];
  }
  if (request.maxDepth !== undefined && (request.maxDepth < 0 || request.maxDepth > LIMITS.maxAllowedDepth)) {
    errors.MaxDepth = [`MaxDepth must be between 0 and ${LIMITS.maxAllowedDepth}.`];
  }
  if (request.maxPages !== undefined && (request.maxPages < 1 || request.maxPages > LIMITS.maxAllowedPages)) {
    errors.MaxPages = [`MaxPages must be between 1 and ${LIMITS.maxAllowedPages}.`];
  }
  return errors;
}

function snapshot(job: MockJob, t: number): Snapshot {
  const startedAtMs = job.createdAtMs + PENDING_MS;
  const total = job.nodes.length;
  const cancelAt = job.cancelRequestedAtMs;

  if (cancelAt !== null && cancelAt < startedAtMs) {
    return { status: 'Canceled', startedAtMs: null, completedAtMs: cancelAt, failureReason: null, discovered: 0, processed: 0 };
  }
  if (t < startedAtMs) {
    return { status: 'Pending', startedAtMs: null, completedAtMs: null, failureReason: null, discovered: 0, processed: 0 };
  }

  const runMs = job.scenario === 'failed' ? RUNNING_MS / 2 : job.scenario === 'rootFailed' ? 600 : RUNNING_MS;
  const naturalEnd = startedAtMs + runMs;
  const canceledAt = cancelAt !== null ? cancelAt + CANCEL_OBSERVED_AFTER_MS : null;
  const canceled = canceledAt !== null && canceledAt < naturalEnd && t >= canceledAt;
  const effective = Math.min(t, canceled && canceledAt !== null ? canceledAt : naturalEnd);
  const p = Math.min(1, (effective - startedAtMs) / runMs);

  let processed = Math.floor(p * total);
  let discovered = Math.min(total, Math.max(1, Math.ceil(p * 1.6 * total) + 1));
  if (p >= 1 && job.scenario !== 'failed') {
    processed = total;
    discovered = total;
  }
  if (job.scenario === 'failed') {
    // The worker gives up part-way; leave a visible backlog of Discovered pages.
    processed = Math.min(processed, Math.floor(total * 0.4));
  }
  discovered = Math.max(discovered, processed);

  if (canceled && canceledAt !== null) {
    return { status: 'Canceled', startedAtMs, completedAtMs: canceledAt, failureReason: null, discovered, processed };
  }
  if (t < naturalEnd) {
    return { status: 'Running', startedAtMs, completedAtMs: null, failureReason: null, discovered, processed };
  }
  if (job.scenario === 'failed') {
    return {
      status: 'Failed',
      startedAtMs,
      completedAtMs: naturalEnd,
      failureReason: 'Processing failed after 5 delivery attempts: the database connection was lost.',
      discovered,
      processed,
    };
  }
  return { status: 'Completed', startedAtMs, completedAtMs: naturalEnd, failureReason: null, discovered, processed };
}

function isTruncated(job: MockJob, snap: Snapshot): boolean {
  return job.truncatedTemplate && snap.discovered >= job.maxPages;
}

function toDetails(job: MockJob, t: number): JobDetailsDto {
  const snap = snapshot(job, t);
  const failed = job.nodes.slice(0, snap.processed).filter((n) => n.fetchStatus === 'Failed').length;
  const terminal = snap.status === 'Completed' || snap.status === 'Failed' || snap.status === 'Canceled';
  return {
    jobId: job.jobId,
    inputUrl: job.inputUrl,
    normalizedStartUrl: job.normalizedStartUrl,
    startingHost: job.startingHost,
    status: snap.status,
    createdAt: new Date(job.createdAtMs).toISOString(),
    startedAt: snap.startedAtMs !== null ? new Date(snap.startedAtMs).toISOString() : null,
    completedAt: snap.completedAtMs !== null ? new Date(snap.completedAtMs).toISOString() : null,
    failureReason: snap.failureReason,
    maxDepth: job.maxDepth,
    maxPages: job.maxPages,
    discoveredPages: snap.discovered,
    processedPages: snap.processed,
    failedPages: failed,
    truncated: isTruncated(job, snap),
    cancellationRequested: job.cancelRequestedAtMs !== null,
    progressPercent: terminal ? 100 : snap.discovered === 0 ? 0 : Math.floor((snap.processed * 100) / snap.discovered),
  };
}

function buildTree(nodes: TemplateNode[], processed: number): TreeNodeDto | null {
  const dtos: TreeNodeDto[] = nodes.map((n, i): TreeNodeDto => {
    const base = { pageId: n.pageId, url: n.url, depth: n.depth, children: [] };
    return i < processed
      ? {
          ...base,
          fetchStatus: n.fetchStatus,
          httpStatusCode: n.httpStatusCode,
          domainLinkRatio: n.domainLinkRatio,
          outgoingLinkCount: n.outgoingLinkCount,
          internalLinkCount: n.internalLinkCount,
          errorMessage: n.errorMessage,
        }
      : {
          ...base,
          fetchStatus: 'Discovered',
          httpStatusCode: null,
          domainLinkRatio: 0,
          outgoingLinkCount: 0,
          internalLinkCount: 0,
          errorMessage: null,
        };
  });
  nodes.forEach((n, i) => {
    const node = dtos[i];
    if (n.parentIndex !== null && node) dtos[n.parentIndex]?.children.push(node);
  });
  return dtos[0] ?? null;
}

function buildJob(init: {
  jobId: string;
  inputUrl: string;
  createdAtMs: number;
  maxDepth: number;
  maxPages: number;
  scenario: Scenario;
}): MockJob {
  const url = new URL(init.inputUrl);
  url.hash = '';
  const { nodes, truncated } = generateTemplate(url, init.maxDepth, init.maxPages, init.scenario, init.jobId);
  return {
    ...init,
    normalizedStartUrl: url.toString(),
    startingHost: url.hostname,
    nodes,
    truncatedTemplate: truncated,
    cancelRequestedAtMs: null,
  };
}

/** Deterministic BFS site layout; failures and skips are placed by index so every run looks the same. */
function generateTemplate(
  start: URL,
  maxDepth: number,
  maxPages: number,
  scenario: Scenario,
  seed: string,
): { nodes: TemplateNode[]; truncated: boolean } {
  const branching = scenario === 'large' ? BRANCHING_LARGE : BRANCHING_NORMAL;
  const nodes: TemplateNode[] = [];
  const queue: number[] = [];

  const push = (parentIndex: number | null, path: string, depth: number): void => {
    const index = nodes.length;
    const isRoot = parentIndex === null;
    let fetchStatus: TemplateNode['fetchStatus'] = 'Fetched';
    let httpStatusCode: number | null = 200;
    let errorMessage: string | null = null;
    let finalPath = path;

    if (isRoot && scenario === 'rootFailed') {
      fetchStatus = 'Failed';
      httpStatusCode = null;
      errorMessage = 'Name or service not known (DNS lookup failed)';
    } else if (!isRoot && index % 11 === 7) {
      fetchStatus = 'Failed';
      httpStatusCode = 404;
      errorMessage = 'HTTP 404 Not Found';
    } else if (!isRoot && index % 23 === 13) {
      fetchStatus = 'Failed';
      httpStatusCode = null;
      errorMessage = 'Request timed out after 10 s';
    } else if (!isRoot && index % 17 === 5) {
      fetchStatus = 'Skipped';
      errorMessage = 'Unsupported content type application/pdf';
      finalPath = `${path}.pdf`;
    }

    const fetched = fetchStatus === 'Fetched';
    const outgoing = fetched ? 5 + ((index * 7) % 13) : 0;
    const internal = fetched ? Math.min(outgoing, Math.round(outgoing * (0.35 + ((index * 37) % 60) / 100))) : 0;
    nodes.push({
      pageId: `${seed}-${index}`,
      parentIndex,
      url: `${start.origin}${finalPath}`,
      depth,
      fetchStatus,
      httpStatusCode,
      domainLinkRatio: outgoing === 0 ? 0 : internal / outgoing,
      outgoingLinkCount: outgoing,
      internalLinkCount: internal,
      errorMessage,
    });
    if (fetched) queue.push(index);
  };

  push(null, start.pathname || '/', 0);
  // Generating one node past maxPages is enough to know the crawl would have been truncated.
  while (queue.length > 0 && nodes.length <= maxPages) {
    const parentIndex = queue.shift()!;
    const parent = nodes[parentIndex]!;
    const fanOut = branching[parent.depth];
    if (parent.depth >= maxDepth || fanOut === undefined) continue;
    const base = new URL(parent.url).pathname.replace(/\/$/, '');
    for (let j = 0; j < fanOut && nodes.length <= maxPages; j++) {
      const word = SECTION_WORDS[(parentIndex * 3 + j) % SECTION_WORDS.length]!;
      const segment = parent.depth === 0 ? word : `${word}-${j + 1}`;
      push(parentIndex, `${base}/${segment}`, parent.depth + 1);
    }
  }

  const truncated = nodes.length > maxPages;
  return { nodes: truncated ? nodes.slice(0, maxPages) : nodes, truncated };
}

function seedJobs(t: number): MockJob[] {
  const minute = 60_000;
  const seeds: Array<{ url: string; scenario: Scenario; maxDepth: number; maxPages: number; ago: number }> = [
    { url: 'https://example.com/', scenario: 'normal', maxDepth: 2, maxPages: 200, ago: 4 * minute },
    { url: 'https://docs.example.org/', scenario: 'normal', maxDepth: 3, maxPages: 15, ago: 26 * minute },
    { url: 'https://broken.example.net/', scenario: 'failed', maxDepth: 2, maxPages: 200, ago: 58 * minute },
    { url: 'https://unreachable.example.io/', scenario: 'rootFailed', maxDepth: 2, maxPages: 200, ago: 95 * minute },
    { url: 'https://large.example.com/', scenario: 'large', maxDepth: 5, maxPages: 1000, ago: 140 * minute },
  ];
  const hosts = ['acme.dev', 'blog.example.com', 'shop.example.co', 'news.example.org', 'wiki.example.net'];
  for (let i = 0; i < 22; i++) {
    seeds.push({
      url: `https://${hosts[i % hosts.length]!}/${i % 3 === 0 ? '' : `section-${i}`}`,
      scenario: 'normal',
      maxDepth: (i % 4) + 1,
      maxPages: i % 5 === 0 ? 10 : 200,
      ago: (180 + i * 47) * minute,
    });
  }
  return seeds.map((s, i) =>
    buildJob({
      jobId: `00000000-0000-4000-8000-${i.toString(16).padStart(12, '0')}`,
      inputUrl: s.url,
      createdAtMs: t - s.ago,
      maxDepth: s.maxDepth,
      maxPages: s.maxPages,
      scenario: s.scenario,
    }),
  );
}
