import { AlertTriangle, ChevronLeft, ChevronRight, Inbox, Plus } from 'lucide-react';
import type { MouseEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useJobs } from '../api/queries';
import { isTerminal, type JobSummaryDto } from '../api/types';
import { Card } from '../components/Card';
import { EmptyState } from '../components/EmptyState';
import { ErrorState } from '../components/ErrorState';
import { Skeleton } from '../components/Skeleton';
import { Spinner } from '../components/Spinner';
import { StatusBadge } from '../components/StatusBadge';
import { formatCount, formatDateTime, formatDuration, formatRelative } from '../lib/format';
import { useNow } from '../lib/useNow';

const HISTORY_PAGE_SIZE = 20;
const RELATIVE_TICK_MS = 15_000;
const SKELETON_ROWS = 6;

function readPage(params: URLSearchParams): number {
  const value = Number(params.get('page'));
  return Number.isInteger(value) && value >= 1 ? value : 1;
}

export function HistoryPage() {
  const [params, setParams] = useSearchParams();
  const page = readPage(params);
  const jobsQuery = useJobs(page, HISTORY_PAGE_SIZE);
  const now = useNow(RELATIVE_TICK_MS);

  const goTo = (next: number) => setParams(next <= 1 ? {} : { page: String(next) });

  const data = jobsQuery.data;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  let body;
  if (jobsQuery.isPending) {
    body = <HistorySkeleton />;
  } else if (jobsQuery.isError && !data) {
    body = (
      <ErrorState
        error={jobsQuery.error}
        title="Could not load crawl history"
        onRetry={() => void jobsQuery.refetch()}
        retrying={jobsQuery.isFetching}
      />
    );
  } else if (data && data.totalCount === 0) {
    body = (
      <EmptyState
        icon={<Inbox size={28} aria-hidden />}
        title="No crawl jobs yet"
        description="Start a crawl to see it listed here."
        action={
          <Link to="/" className="btn btn--primary">
            <Plus size={16} aria-hidden />
            Start crawl
          </Link>
        }
      />
    );
  } else if (data && data.items.length === 0) {
    body = (
      <EmptyState
        title={`Page ${page} is empty`}
        description={`There are ${totalPages} page(s) of results.`}
        action={
          <button type="button" className="btn btn--secondary" onClick={() => goTo(1)}>
            Go to first page
          </button>
        }
      />
    );
  } else if (data) {
    body = (
      <>
        <div className={`table-wrap${jobsQuery.isPlaceholderData ? ' is-stale' : ''}`}>
          <JobsTable jobs={data.items} now={now} />
        </div>
        <Pagination
          page={page}
          totalPages={totalPages}
          totalCount={data.totalCount}
          onChange={goTo}
          loading={jobsQuery.isPlaceholderData}
        />
      </>
    );
  }

  return (
    <div className="page">
      <header className="page-header page-header--row">
        <div>
          <h1 className="page-title">History</h1>
          <p className="page-description">All crawl jobs, newest first.</p>
        </div>
        <div className="page-header__actions">
          {jobsQuery.isFetching && !jobsQuery.isPending && <Spinner size={16} label="Updating" />}
          <Link to="/" className="btn btn--primary">
            <Plus size={16} aria-hidden />
            Start crawl
          </Link>
        </div>
      </header>
      {jobsQuery.isRefetchError && data && (
        <p className="field__error" role="alert">
          Could not refresh the list; showing the last loaded results.
        </p>
      )}
      <Card className="card--flush">{body}</Card>
    </div>
  );
}

function JobsTable({ jobs, now }: { jobs: JobSummaryDto[]; now: number }) {
  const navigate = useNavigate();

  // The URL cell holds the real link (keyboard + middle-click); the row click is a mouse convenience.
  const onRowClick = (event: MouseEvent<HTMLTableRowElement>, jobId: string) => {
    if ((event.target as HTMLElement).closest('a, button')) return;
    if (window.getSelection()?.toString()) return;
    navigate(`/jobs/${jobId}`);
  };

  return (
    <table className="table">
      <thead>
        <tr>
          <th scope="col">Status</th>
          <th scope="col">Start URL</th>
          <th scope="col">Created</th>
          <th scope="col">Duration</th>
          <th scope="col" className="num">
            Processed
          </th>
          <th scope="col" className="num">
            Failed
          </th>
        </tr>
      </thead>
      <tbody>
        {jobs.map((job) => (
          <tr key={job.jobId} className="table__row--clickable" onClick={(e) => onRowClick(e, job.jobId)}>
            <td data-label="Status">
              <StatusBadge status={job.status} />
            </td>
            <td data-label="Start URL">
              <div className="table__url">
              <Link to={`/jobs/${job.jobId}`} title={job.inputUrl} className="truncate mono">
                {job.inputUrl}
              </Link>
              {job.truncated && (
                <span className="chip chip--warning" title="Stopped at maxPages; more in-scope pages were discovered">
                  <AlertTriangle size={12} aria-hidden />
                  Truncated
                </span>
              )}
              </div>
            </td>
            <td data-label="Created">
              <span className="time-value">
                <span title={job.createdAt}>{formatDateTime(job.createdAt)}</span>
                <span className="muted">{formatRelative(job.createdAt, now)}</span>
              </span>
            </td>
            <td data-label="Duration" className="tabular">
              <span>
                {formatDuration(job.startedAt, job.completedAt, now)}
                {!isTerminal(job.status) && job.startedAt && <span className="muted"> so far</span>}
              </span>
            </td>
            <td data-label="Processed" className="num tabular">
              <span>
                {formatCount(job.processedPages)}
                <span className="muted"> / {formatCount(job.discoveredPages)}</span>
              </span>
            </td>
            <td data-label="Failed" className={`num tabular${job.failedPages > 0 ? ' text-danger' : ''}`}>
              {formatCount(job.failedPages)}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Pagination({
  page,
  totalPages,
  totalCount,
  onChange,
  loading,
}: {
  page: number;
  totalPages: number;
  totalCount: number;
  onChange: (page: number) => void;
  loading: boolean;
}) {
  return (
    <nav className="pagination" aria-label="Pagination">
      <span className="muted">
        {formatCount(totalCount)} {totalCount === 1 ? 'job' : 'jobs'}
      </span>
      <div className="pagination__controls">
        <button
          type="button"
          className="btn btn--secondary btn--sm"
          onClick={() => onChange(page - 1)}
          disabled={page <= 1 || loading}
        >
          <ChevronLeft size={14} aria-hidden />
          Previous
        </button>
        <span className="pagination__label" aria-current="page">
          Page {page} of {totalPages}
        </span>
        <button
          type="button"
          className="btn btn--secondary btn--sm"
          onClick={() => onChange(page + 1)}
          disabled={page >= totalPages || loading}
        >
          Next
          <ChevronRight size={14} aria-hidden />
        </button>
      </div>
    </nav>
  );
}

function HistorySkeleton() {
  return (
    <div className="table-skeleton" aria-busy="true" aria-label="Loading crawl history">
      {Array.from({ length: SKELETON_ROWS }, (_, i) => (
        <div className="table-skeleton__row" key={i}>
          <Skeleton width="5.5rem" height="1.25rem" />
          <Skeleton width="40%" />
          <Skeleton width="8rem" />
          <Skeleton width="3rem" />
        </div>
      ))}
    </div>
  );
}
