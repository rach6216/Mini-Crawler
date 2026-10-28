import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { node, sampleTree } from '../test/fixtures';
import { TreeView } from './TreeView';

afterEach(cleanup);

const visiblePaths = () =>
  within(screen.getByRole('list', { name: 'Crawled pages' }))
    .getAllByRole('listitem')
    .map((item) => item.querySelector('.tree-row__path')?.textContent);

describe('TreeView', () => {
  it('renders the root expanded to depth 1 and toggles children', () => {
    render(<TreeView root={sampleTree()} />);

    expect(visiblePaths()).toEqual(['/', '/a', '/b']);
    expect(screen.getByText('Showing 3 of 7 pages')).toBeTruthy();

    const expandA = screen.getByRole('button', { name: /^Expand \/a / });
    expect(expandA.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(expandA);
    expect(visiblePaths()).toEqual(['/', '/a', '/a1', '/a2', '/b']);
    expect(screen.getByRole('button', { name: /^Collapse \/a / }).getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(screen.getByRole('button', { name: /^Collapse \/ / }));
    expect(visiblePaths()).toEqual(['/']);
  });

  it('expands and collapses everything', () => {
    render(<TreeView root={sampleTree()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
    expect(visiblePaths()).toHaveLength(7);
    fireEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
    expect(visiblePaths()).toEqual(['/']);
  });

  it('keeps expansion keyed by pageId across live updates', () => {
    const { rerender } = render(<TreeView root={sampleTree()} />);
    fireEvent.click(screen.getByRole('button', { name: /^Expand \/b / }));
    const grown = sampleTree();
    grown.children[1]!.children.push(node('b2', [], { depth: 2 }));
    rerender(<TreeView root={grown} />);
    expect(visiblePaths()).toEqual(['/', '/a', '/b', '/b1', '/b2']);
  });

  it('shows failure details for failed pages', () => {
    const root = node('root', [
      node('gone', [], { fetchStatus: 'Failed', httpStatusCode: 404, errorMessage: 'HTTP 404 Not Found', depth: 1 }),
    ]);
    render(<TreeView root={root} />);
    expect(screen.getByText('HTTP 404 Not Found')).toBeTruthy();
    expect(screen.getByText('Failed').closest('.badge')?.getAttribute('title')).toBe('HTTP 404 · HTTP 404 Not Found');
  });
});
