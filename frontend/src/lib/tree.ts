import type { TreeNodeDto } from '../api/types';
import { TREE_DEFAULT_EXPANDED_DEPTH } from '../config';

export interface VisibleRow {
  node: TreeNodeDto;
  level: number;
  hasChildren: boolean;
  expanded: boolean;
}

/** Iterative so very deep trees cannot overflow the call stack. */
export function countNodes(root: TreeNodeDto | null | undefined): number {
  if (!root) return 0;
  let count = 0;
  const stack: TreeNodeDto[] = [root];
  while (stack.length > 0) {
    const node = stack.pop()!;
    count++;
    for (const child of node.children) stack.push(child);
  }
  return count;
}

/**
 * Pre-order list of the rows that are currently visible: a node's children are included only
 * when the node's pageId is in `expanded`. Rendering this flat list avoids deep React recursion.
 */
export function flattenVisible(root: TreeNodeDto | null | undefined, expanded: ReadonlySet<string>): VisibleRow[] {
  if (!root) return [];
  const rows: VisibleRow[] = [];
  const stack: Array<{ node: TreeNodeDto; level: number }> = [{ node: root, level: 0 }];
  while (stack.length > 0) {
    const { node, level } = stack.pop()!;
    const hasChildren = node.children.length > 0;
    const isExpanded = hasChildren && expanded.has(node.pageId);
    rows.push({ node, level, hasChildren, expanded: isExpanded });
    if (isExpanded) {
      for (let i = node.children.length - 1; i >= 0; i--) stack.push({ node: node.children[i]!, level: level + 1 });
    }
  }
  return rows;
}

/** pageIds of nodes with children whose level is below `depth` (depth 1 => only the root is expanded). */
export function expandedToDepth(root: TreeNodeDto | null | undefined, depth: number = TREE_DEFAULT_EXPANDED_DEPTH): Set<string> {
  const ids = new Set<string>();
  if (!root) return ids;
  const stack: Array<{ node: TreeNodeDto; level: number }> = [{ node: root, level: 0 }];
  while (stack.length > 0) {
    const { node, level } = stack.pop()!;
    if (level >= depth || node.children.length === 0) continue;
    ids.add(node.pageId);
    for (const child of node.children) stack.push({ node: child, level: level + 1 });
  }
  return ids;
}

export function expandedAll(root: TreeNodeDto | null | undefined): Set<string> {
  return expandedToDepth(root, Number.POSITIVE_INFINITY);
}

/**
 * Expansion is stored as a baseline plus per-node overrides so that nodes arriving through
 * live polling follow the current baseline ("expand all" keeps expanding new nodes).
 */
export type ExpansionBaseline = 'default' | 'all' | 'none';

export interface ExpansionState {
  baseline: ExpansionBaseline;
  overrides: ReadonlyMap<string, boolean>;
}

export const INITIAL_EXPANSION: ExpansionState = { baseline: 'default', overrides: new Map() };

export function resolveExpanded(root: TreeNodeDto | null | undefined, state: ExpansionState): Set<string> {
  const base =
    state.baseline === 'all' ? expandedAll(root) : state.baseline === 'default' ? expandedToDepth(root) : new Set<string>();
  for (const [id, open] of state.overrides) {
    if (open) base.add(id);
    else base.delete(id);
  }
  return base;
}

export function toggleExpansion(state: ExpansionState, pageId: string, currentlyExpanded: boolean): ExpansionState {
  const overrides = new Map(state.overrides);
  overrides.set(pageId, !currentlyExpanded);
  return { baseline: state.baseline, overrides };
}
