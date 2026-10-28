import { describe, expect, it } from 'vitest';
import { node, sampleTree } from '../test/fixtures';
import {
  INITIAL_EXPANSION,
  countNodes,
  expandedAll,
  expandedToDepth,
  flattenVisible,
  resolveExpanded,
  toggleExpansion,
} from './tree';

const ids = (rows: ReturnType<typeof flattenVisible>) => rows.map((r) => r.node.pageId);

describe('countNodes', () => {
  it('counts every node including the root', () => {
    expect(countNodes(sampleTree())).toBe(7);
  });

  it('returns 0 for a missing tree and 1 for a leaf root', () => {
    expect(countNodes(null)).toBe(0);
    expect(countNodes(node('root'))).toBe(1);
  });

  it('handles very deep trees without recursion limits', () => {
    let deep = node('n-0');
    for (let i = 1; i < 20_000; i++) deep = node(`n-${i}`, [deep]);
    expect(countNodes(deep)).toBe(20_000);
  });
});

describe('flattenVisible', () => {
  it('shows only the root when nothing is expanded', () => {
    const rows = flattenVisible(sampleTree(), new Set());
    expect(ids(rows)).toEqual(['root']);
    expect(rows[0]).toMatchObject({ level: 0, hasChildren: true, expanded: false });
  });

  it('includes children only of expanded nodes, in pre-order and child order', () => {
    const rows = flattenVisible(sampleTree(), new Set(['root', 'b']));
    expect(ids(rows)).toEqual(['root', 'a', 'b', 'b1']);
    expect(rows.map((r) => r.level)).toEqual([0, 1, 1, 2]);
  });

  it('does not reveal descendants of a collapsed node even if they are in the set', () => {
    const rows = flattenVisible(sampleTree(), new Set(['root', 'b1']));
    expect(ids(rows)).toEqual(['root', 'a', 'b']);
  });

  it('lists the whole tree depth-first when everything is expanded', () => {
    const tree = sampleTree();
    expect(ids(flattenVisible(tree, expandedAll(tree)))).toEqual(['root', 'a', 'a1', 'a2', 'b', 'b1', 'b1x']);
  });

  it('never marks leaves as expanded', () => {
    const rows = flattenVisible(node('root', [node('leaf')]), new Set(['root', 'leaf']));
    expect(rows[1]).toMatchObject({ hasChildren: false, expanded: false });
  });
});

describe('expansion state', () => {
  it('expands only the root by default (depth 1)', () => {
    expect([...expandedToDepth(sampleTree())]).toEqual(['root']);
  });

  it('applies per-node overrides on top of the baseline', () => {
    const tree = sampleTree();
    const opened = toggleExpansion(INITIAL_EXPANSION, 'a', false);
    expect(ids(flattenVisible(tree, resolveExpanded(tree, opened)))).toEqual(['root', 'a', 'a1', 'a2', 'b']);
    const closedRoot = toggleExpansion(opened, 'root', true);
    expect(ids(flattenVisible(tree, resolveExpanded(tree, closedRoot)))).toEqual(['root']);
  });

  it('keeps expanding nodes that arrive later under the "all" baseline', () => {
    const state = { baseline: 'all' as const, overrides: new Map<string, boolean>() };
    const later = node('root', [node('a', [node('a1', [node('new')])])]);
    expect(ids(flattenVisible(later, resolveExpanded(later, state)))).toEqual(['root', 'a', 'a1', 'new']);
  });
});
