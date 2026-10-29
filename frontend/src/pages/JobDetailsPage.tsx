import { ArrowLeft, ExternalLink, Hourglass, RefreshCw, SearchX, Square } from 'lucide-react';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ApiError, describeError } from '../api/errors';
import { isTreeAvailable, useCancelJob, useJob, useJobTree } from '../api/queries';
import { isTerminal, type JobDetailsDto, type JobTreeDto } from '../api/types';
import { Alert } from '../components/Alert';
import { Card } from '../components/Card';
import { CopyButton } from '../components/CopyButton';
import { EmptyState } from '../components/EmptyState';
import { ErrorState } from '../components/ErrorState';
import { KeyValueList } from '../components/KeyValueList';
import { ProgressBar } from '../components/ProgressBar';
import { Skeleton } from '../components/Skeleton';
import { Spinner } from '../components/Spinner';
import { StatusBadge } from '../components/StatusBadge';
import { TreeView } from '../components/TreeView';
import { formatCount, formatDateTime, formatDuration, formatRelative } from '../lib/format';
import { useNow } from '../lib/useNow';

const LIVE_TICK_MS = 1_000;
const IDLE_TICK_MS = 30_000;

export function JobDetailsPage() {
  const { jobId = '' } = useParams<{ jobId: string }>();
  const jobQuery = useJob(jobId);
  const treeQuery = useJobTree(jobId, jobQuery.data);
  // Tracks only user-initiated refreshes so background polling does not flicker the button.
  const [refreshing, setRefreshing] = useState(false);

  if (jobQuery.isPending) return <JobDetailsSkeleton />;

  if (jobQuery.isLoadingError) {
    if (jobQuery.error instanceof ApiError && jobQuery.error.isNotFound) {
      return (
        <div className="page">
          <EmptyState
            icon={<SearchX size={28} aria-hidden />}
            title="Job not found"
            description={
              <>
                No crawl job exists with id <code>{jobId}</code>.
              </>
            }
            action={
              <Link to="/history" className="btn btn--secondary">
                <ArrowLeft size={16} aria-hidden />
                Back to history
              </Link>
            }
          />
        </div>
      );
    }
    return (
      <div className="page">
        <ErrorState
          error={jobQuery.error}
          title="Could not load this job"
          onRetry={() => void jobQuery.refetch()}
          retrying={jobQuery.isFetching}
        />
      </div>
    );
  }

  const job = jobQuery.data;
  const refresh = () => {
    setRefreshing(true);
    void Promise.all([jobQuery.refetch(), isTreeAvailable(job) ? treeQuery.refetch() : null]).finally(() =>
      setRefreshing(false),
    );
  };

  return (
    <div className="page">
      <JobHeader job={job} onRefresh={refresh} refreshing={refreshing} />
      {jobQuery.isRefetchError && (
        <Alert tone="warning" title="Live updates paused">
          {describeError(jobQuery.error).detail ?? 'The latest status could not be loaded.'} Showing the last known state.
        </Alert>
      )}
      <JobWarnings job={job} tree={treeQuery.data} />

      <div className="grid grid--3">
        <TimelineCard job={job} />
        <ProgressCard job={job} />
        <LimitsCard job={job} />
      </div>

      <TreeSection job={job} treeQuery={treeQuery} />
    </div>
  );
}

function JobHeader({ job, onRefresh, refreshing }: { job: JobDetailsDto; onRefresh: () => void; refreshing: boolean }) {
  return (
    <header className="job-header">
      <div className="job-header__main">
        <Link to="/history" className="back-link">
          <ArrowLeft size={14} aria-hidden />
          History
        </Link>
        <div className="job-header__title-row">
          <h1 className="page-title job-header__title">
            <a href={job.inputUrl} target="_blank" rel="noopener noreferrer" title={job.inputUrl} className="truncate-link">
              <span className="truncate">{job.inputUrl}</span>
              <ExternalLink size={16} aria-hidden className="job-header__ext" />
            </a>
          </h1>
          <StatusBadge status={job.status} />
        </div>
        <p className="sr-only" aria-live="polite" aria-atomic>
          Job status: {job.status}
          {job.cancellationRequested && !isTerminal(job.status) ? ', cancellation requested' : ''}
        </p>
        <div className="job-header__id">
          <span className="muted">Job</span>
          <code className="mono">{job.jobId}</code>
          <CopyButton value={job.jobId} label="Copy job id" />
        </div>
      </div>
      <div className="job-header__actions">
        <button type="button" className="btn btn--secondary" onClick={onRefresh} disabled={refreshing}>
          <RefreshCw size={16} aria-hidden className={refreshing ? 'spin' : undefined} />
          Refresh
        </button>
        <CancelControl job={job} />
      </div>
    </header>
  );
}

function CancelControl({ job }: { job: JobDetailsDto }) {
  const cancel = useCancelJob(job.jobId);
  if (isTerminal(job.status)) return null;

  if (job.cancellationRequested) {
    return (
      <button type="button" className="btn btn--danger-outline" disabled>
        <Spinner size={16} />
        Cancellation requested…
      </button>
    );
  }

  return (
    <div className="cancel-control">
      <button
        type="button"
        className="btn btn--danger-outline"
        onClick={() => cancel.mutate()}
        disabled={cancel.isPending}
      >
        {cancel.isPending ? <Spinner size={16} /> : <Square size={14} aria-hidden />}
        {cancel.isPending ? 'Canceling…' : 'Cancel crawl'}
      </button>
      {cancel.isError && (
        <p className="field__error" role="alert">
          {describeError(cancel.error).detail ?? describeError(cancel.error).title}
        </p>
      )}
    </div>
  );
}

function JobWarnings({ job, tree }: { job: JobDetailsDto; tree: JobTreeDto | undefined }) {
  const rootFailed = tree?.root?.fetchStatus === 'Failed';
  return (
    <div className="stack">
      {job.status === 'Failed' && (
        <Alert tone="danger" title="The crawl failed">
          {job.failureReason ?? 'No failure reason was recorded.'}
        </Alert>
      )}
      {rootFailed && (
        <Alert tone="danger" title="The start page could not be fetched">
          {tree?.root?.errorMessage ?? 'No pages beyond the start URL could be discovered.'}
        </Alert>
      )}
      {job.truncated && (
        <Alert tone="warning" title="Stopped at maxPages; more in-scope pages were discovered">
          The tree contains the first {formatCount(job.maxPages)} pages. Increase Max pages to crawl further.
        </Alert>
      )}
      {job.failedPages > 0 && !(rootFailed && job.failedPages === 1) && (
        <Alert tone="warning" title={`${formatCount(job.failedPages)} page(s) could not be fetched`}>
          Failed pages are marked in the tree with their HTTP status or error.
        </Alert>
      )}
    </div>
  );
}

function TimelineCard({ job }: { job: JobDetailsDto }) {
  const live = !isTerminal(job.status);
  // Running durations need a per-second clock; relative times of finished jobs only drift slowly.
  const now = useNow(live ? LIVE_TICK_MS : IDLE_TICK_MS);
  return (
    <Card title="Timeline">
      <KeyValueList
        items={[
          { label: 'Created', value: <TimeValue iso={job.createdAt} now={now} />, title: job.createdAt },
          { label: 'Started', value: <TimeValue iso={job.startedAt} now={now} />, title: job.startedAt ?? undefined },
          {
            label: 'Completed',
            value: <TimeValue iso={job.completedAt} now={now} />,
            title: job.completedAt ?? undefined,
          },
          {
            label: 'Duration',
            value: (
              <span className="tabular">
                {formatDuration(job.startedAt, job.completedAt, now)}
                {live && job.startedAt && <span className="muted"> (running)</span>}
              </span>
            ),
          },
        ]}
      />
    </Card>
  );
}

function TimeValue({ iso, now }: { iso: string | null; now: number }) {
  if (!iso) return <span className="muted">—</span>;
  return (
    <span className="time-value">
      <span>{formatDateTime(iso)}</span>
      <span className="muted">{formatRelative(iso, now)}</span>
    </span>
  );
}

function ProgressCard({ job }: { job: JobDetailsDto }) {
  return (
    <Card title="Progress">
      <div className="progress-block">
        <div className="progress-block__head">
          <span className="progress-block__value tabular">{job.progressPercent}%</span>
          <span className="muted">
            {formatCount(job.processedPages)} of {formatCount(job.discoveredPages)} pages
          </span>
        </div>
        <ProgressBar value={job.progressPercent} label="Crawl progress" />
        <dl className="counters">
          <div className="counter">
            <dt>Discovered</dt>
            <dd className="tabular">{formatCount(job.discoveredPages)}</dd>
          </div>
          <div className="counter">
            <dt>Processed</dt>
            <dd className="tabular">{formatCount(job.processedPages)}</dd>
          </div>
          <div className={`counter${job.failedPages > 0 ? ' counter--danger' : ''}`}>
            <dt>Failed</dt>
            <dd className="tabular">{formatCount(job.failedPages)}</dd>
          </div>
        </dl>
        {!isTerminal(job.status) && (
          <p className="note">Percentage is relative to pages discovered so far, so it can drop as new links are found.</p>
        )}
      </div>
    </Card>
  );
}

function LimitsCard({ job }: { job: JobDetailsDto }) {
  return (
    <Card title="Limits">
      <KeyValueList
        items={[
          { label: 'Max depth', value: <span className="tabular">{job.maxDepth}</span> },
          { label: 'Max pages', value: <span className="tabular">{formatCount(job.maxPages)}</span> },
          { label: 'Host', value: <code className="mono">{job.startingHost}</code> },
          {
            label: 'Start URL',
            value: (
              <code className="mono truncate" title={job.normalizedStartUrl}>
                {job.normalizedStartUrl}
              </code>
            ),
          },
        ]}
      />
    </Card>
  );
}

function TreeSection({ job, treeQuery }: { job: JobDetailsDto; treeQuery: ReturnType<typeof useJobTree> }) {
  const tree = treeQuery.data;
  const live = !isTerminal(job.status);

  let content;
  if (!isTreeAvailable(job) || (tree && !tree.root && live)) {
    content = (
      <EmptyState
        icon={<Hourglass size={24} aria-hidden />}
        title="Waiting for the first page…"
        description="The tree appears as soon as the worker stores the start page."
      />
    );
  } else if (treeQuery.isPending) {
    content = (
      <div className="center-pad">
        <Spinner label="Loading page tree…" />
      </div>
    );
  } else if (treeQuery.isError && !tree) {
    content = (
      <ErrorState
        error={treeQuery.error}
        title="Could not load the page tree"
        onRetry={() => void treeQuery.refetch()}
        retrying={treeQuery.isFetching}
      />
    );
  } else if (!tree?.root) {
    content = (
      <EmptyState
        title="No pages were crawled"
        description={job.status === 'Canceled' ? 'The job was canceled before any page was stored.' : undefined}
      />
    );
  } else {
    content = (
      <>
        {treeQuery.isError && (
          <Alert tone="warning" title="Tree updates paused">
            {describeError(treeQuery.error).detail ?? 'The latest tree could not be loaded.'}{' '}
            <button type="button" className="link-button" onClick={() => void treeQuery.refetch()}>
              Retry
            </button>
          </Alert>
        )}
        <TreeView key={job.jobId} jobId={job.jobId} root={tree.root} />
      </>
    );
  }

  return (
    <Card
      title="Page tree"
      subtitle={
        tree?.root
          ? `${formatCount(tree.totalNodes)} pages · first-discovery tree, each page appears once${live ? ' · updating live' : ''}`
          : undefined
      }
      actions={live && treeQuery.isFetching ? <Spinner size={14} /> : undefined}
    >
      {content}
    </Card>
  );
}

function JobDetailsSkeleton() {
  return (
    <div className="page" aria-busy="true" aria-label="Loading job">
      <div className="job-header">
        <div className="job-header__main stack">
          <Skeleton width="5rem" />
          <Skeleton width="min(28rem, 80%)" height="1.5rem" />
          <Skeleton width="18rem" />
        </div>
      </div>
      <div className="grid grid--3">
        {[0, 1, 2].map((i) => (
          <Card key={i}>
            <div className="stack">
              <Skeleton width="40%" />
              <Skeleton width="80%" />
              <Skeleton width="65%" />
              <Skeleton width="70%" />
            </div>
          </Card>
        ))}
      </div>
    </div>
  );
}
