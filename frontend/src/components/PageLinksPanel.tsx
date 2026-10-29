import type { ReactNode } from 'react';
import { describeError } from '../api/errors';
import { useJobPageLinks } from '../api/queries';
import { FetchStatusBadge } from './StatusBadge';
import { Spinner } from './Spinner';

export interface PageLinksPanelProps {
  id: string;
  jobId: string;
  pageId: string;
  /** Path of the source page, used to label the list for assistive technology. */
  pagePath: string;
}

/** Discovered outgoing links of one page. Mounted only while its disclosure is open, so loading is lazy. */
export function PageLinksPanel({ id, jobId, pageId, pagePath }: PageLinksPanelProps) {
  const query = useJobPageLinks(jobId, pageId, true);

  let content: ReactNode;
  if (query.isPending) {
    content = <Spinner size={14} label="Loading links…" />;
  } else if (query.isError) {
    content = (
      <p className="page-links__error" role="alert">
        {describeError(query.error).detail ?? 'The links could not be loaded.'}{' '}
        <button type="button" className="link-button" onClick={() => void query.refetch()}>
          Retry
        </button>
      </p>
    );
  } else {
    const links = query.data.pages.flatMap((page) => page.items);
    const total = query.data.pages[0]?.totalCount ?? 0;
    content =
      links.length === 0 ? (
        <p className="muted">No outgoing links were recorded for this page.</p>
      ) : (
        <>
          <ul className="page-links__list" aria-label={`Outgoing links of ${pagePath}`}>
            {links.map((link) => (
              <li key={link.targetUrl} className="page-links__item">
                <a
                  href={link.targetUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  title={link.targetUrl}
                  className="page-links__url"
                >
                  {link.targetUrl}
                </a>
                {link.occurrenceCount > 1 && (
                  <span className="page-links__occurrences" title={`Linked ${link.occurrenceCount} times on this page`}>
                    ×{link.occurrenceCount}
                  </span>
                )}
                <span className={`badge badge--sm ${link.isInternal ? 'badge--info' : 'badge--neutral'}`}>
                  {link.isInternal ? 'Internal' : 'External'}
                </span>
                {link.targetFetchStatus && <FetchStatusBadge status={link.targetFetchStatus} />}
              </li>
            ))}
          </ul>
          {query.hasNextPage && (
            <button
              type="button"
              className="btn btn--ghost btn--sm page-links__more"
              disabled={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
            >
              {query.isFetchingNextPage ? 'Loading…' : `Show more (${links.length.toLocaleString()} of ${total.toLocaleString()})`}
            </button>
          )}
        </>
      );
  }

  return (
    <div id={id} className="page-links">
      {content}
    </div>
  );
}
