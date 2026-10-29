import { ChevronRight, Link2 } from 'lucide-react';
import { memo, type CSSProperties } from 'react';
import type { TreeNodeDto } from '../api/types';
import { splitUrl } from '../lib/format';
import { PageLinksPanel } from './PageLinksPanel';
import { RatioMeter } from './RatioMeter';
import { FetchStatusBadge } from './StatusBadge';

export interface TreeNodeProps {
  jobId: string;
  node: TreeNodeDto;
  level: number;
  hasChildren: boolean;
  expanded: boolean;
  onToggle: (pageId: string, expanded: boolean) => void;
  linksOpen: boolean;
  onToggleLinks: (pageId: string) => void;
}

function TreeNodeRow({ jobId, node, level, hasChildren, expanded, onToggle, linksOpen, onToggleLinks }: TreeNodeProps) {
  const { host, path } = splitUrl(node.url);
  const fetched = node.fetchStatus === 'Fetched';
  const statusTitle = [node.httpStatusCode != null ? `HTTP ${node.httpStatusCode}` : null, node.errorMessage]
    .filter(Boolean)
    .join(' · ');
  const linksPanelId = `page-links-${node.pageId}`;

  return (
    <li className="tree-row" style={{ '--level': level } as CSSProperties} data-status={node.fetchStatus}>
      <div className="tree-row__main">
        {hasChildren ? (
          <button
            type="button"
            className="tree-row__toggle"
            aria-expanded={expanded}
            aria-label={`${expanded ? 'Collapse' : 'Expand'} ${path} (${node.children.length} child pages)`}
            onClick={() => onToggle(node.pageId, expanded)}
          >
            <ChevronRight size={16} aria-hidden className={expanded ? 'tree-row__chevron--open' : undefined} />
          </button>
        ) : (
          <span className="tree-row__toggle tree-row__toggle--leaf" aria-hidden />
        )}
        <div className="tree-row__url">
          <a href={node.url} target="_blank" rel="noopener noreferrer" title={node.url} className="tree-row__link">
            <span className="tree-row__path">{path}</span>
            <span className="tree-row__host">{host}</span>
          </a>
          {node.errorMessage && <span className="tree-row__error">{node.errorMessage}</span>}
          {(node.outgoingLinkCount > 0 || (hasChildren && !expanded)) && (
            <span className="tree-row__extras">
              {hasChildren && !expanded && <span className="tree-row__count">{node.children.length} children</span>}
              {node.outgoingLinkCount > 0 && (
                <button
                  type="button"
                  className="tree-row__links-toggle"
                  aria-expanded={linksOpen}
                  aria-controls={linksOpen ? linksPanelId : undefined}
                  onClick={() => onToggleLinks(node.pageId)}
                >
                  <Link2 size={12} aria-hidden />
                  Links ({node.outgoingLinkCount.toLocaleString()})
                </button>
              )}
            </span>
          )}
        </div>
      </div>
      <div className="tree-row__meta">
        <FetchStatusBadge
          status={node.fetchStatus}
          httpStatusCode={node.httpStatusCode}
          title={statusTitle || undefined}
        />
        <span className="tree-row__ratio">{fetched ? <RatioMeter ratio={node.domainLinkRatio} /> : <span className="muted">—</span>}</span>
        <span className="tree-row__links" title="Internal links / total links">
          {fetched ? (
            <>
              {node.internalLinkCount.toLocaleString()}
              <span className="muted"> / {node.outgoingLinkCount.toLocaleString()}</span>
            </>
          ) : (
            <span className="muted">—</span>
          )}
        </span>
      </div>
      {linksOpen && <PageLinksPanel id={linksPanelId} jobId={jobId} pageId={node.pageId} pagePath={path} />}
    </li>
  );
}

export const TreeNode = memo(TreeNodeRow);
