import { ChevronsDownUp, ChevronsUpDown } from 'lucide-react';
import { useCallback, useMemo, useState } from 'react';
import type { TreeNodeDto } from '../api/types';
import {
  INITIAL_EXPANSION,
  countNodes,
  flattenVisible,
  resolveExpanded,
  toggleExpansion,
  type ExpansionState,
} from '../lib/tree';
import { TreeNode } from './TreeNode';

/**
 * Renders the crawl tree as a flat list of visible rows (pre-order), so a 1000-page tree costs
 * one list render rather than deep component recursion. Expansion state is keyed by pageId and
 * survives live polling updates. Mount with `key={jobId}` to reset it (and the open link panels) per job.
 */
export function TreeView({ jobId, root }: { jobId: string; root: TreeNodeDto }) {
  const [expansion, setExpansion] = useState<ExpansionState>(INITIAL_EXPANSION);
  // Pages whose outgoing-link panel is open; only visible rows render a panel, so hidden ones cost nothing.
  const [linksOpen, setLinksOpen] = useState<ReadonlySet<string>>(() => new Set());

  const total = useMemo(() => countNodes(root), [root]);
  const expanded = useMemo(() => resolveExpanded(root, expansion), [root, expansion]);
  const rows = useMemo(() => flattenVisible(root, expanded), [root, expanded]);

  const onToggle = useCallback((pageId: string, isExpanded: boolean) => {
    setExpansion((state) => toggleExpansion(state, pageId, isExpanded));
  }, []);

  const onToggleLinks = useCallback((pageId: string) => {
    setLinksOpen((open) => {
      const next = new Set(open);
      if (!next.delete(pageId)) next.add(pageId);
      return next;
    });
  }, []);

  return (
    <div className="tree">
      <div className="tree__toolbar">
        <p className="tree__summary">
          Showing {rows.length.toLocaleString()} of {total.toLocaleString()} {total === 1 ? 'page' : 'pages'}
        </p>
        <div className="tree__actions">
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setExpansion({ baseline: 'all', overrides: new Map() })}
          >
            <ChevronsUpDown size={14} aria-hidden />
            Expand all
          </button>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setExpansion({ baseline: 'none', overrides: new Map() })}
          >
            <ChevronsDownUp size={14} aria-hidden />
            Collapse all
          </button>
        </div>
      </div>
      <div className="tree__header" aria-hidden>
        <span>Page</span>
        <span>Fetch status</span>
        <span>Domain link ratio</span>
        <span>Internal / total links</span>
      </div>
      <ul className="tree__list" aria-label="Crawled pages">
        {rows.map((row) => (
          <TreeNode
            key={row.node.pageId}
            jobId={jobId}
            node={row.node}
            level={row.level}
            hasChildren={row.hasChildren}
            expanded={row.expanded}
            onToggle={onToggle}
            linksOpen={linksOpen.has(row.node.pageId)}
            onToggleLinks={onToggleLinks}
          />
        ))}
      </ul>
    </div>
  );
}
