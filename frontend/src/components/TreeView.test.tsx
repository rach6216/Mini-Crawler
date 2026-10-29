import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api';
import { node, sampleTree } from '../test/fixtures';
import { TreeView as TreeViewComponent } from './TreeView';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const JOB_ID = 'job-1';

function TreeView({ root }: { root: Parameters<typeof TreeViewComponent>[0]['root'] }): ReactElement {
  return <TreeViewComponent jobId={JOB_ID} root={root} />;
}

function renderWithQueries(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

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

  it('lazily loads and discloses the outgoing links of a page', async () => {
    const getPageLinks = vi.spyOn(api, 'getPageLinks').mockResolvedValue({
      items: [
        { targetUrl: 'https://example.com/a', occurrenceCount: 2, isInternal: true, targetPageId: 'a', targetFetchStatus: 'Fetched' },
        { targetUrl: 'https://external.example.org/x', occurrenceCount: 1, isInternal: false, targetPageId: null, targetFetchStatus: null },
      ],
      page: 1,
      pageSize: 100,
      totalCount: 2,
    });
    const root = node('root', [node('leaf', [], { depth: 1, outgoingLinkCount: 0, internalLinkCount: 0 })]);
    renderWithQueries(<TreeView root={root} />);

    // No button for a page without links; nothing is fetched until the disclosure is opened.
    expect(screen.getAllByRole('button', { name: /^Links/ })).toHaveLength(1);
    expect(getPageLinks).not.toHaveBeenCalled();

    const toggle = screen.getByRole('button', { name: 'Links (4)' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');

    const list = await screen.findByRole('list', { name: 'Outgoing links of /' });
    expect(getPageLinks).toHaveBeenCalledWith(JOB_ID, 'root', 1, 100, expect.anything());
    expect(toggle.getAttribute('aria-controls')).toBe(list.parentElement?.id);
    const [internal, external] = within(list).getAllByRole('listitem');
    expect(within(internal!).getByRole('link').getAttribute('title')).toBe('https://example.com/a');
    expect(internal!.textContent).toContain('×2');
    expect(internal!.textContent).toContain('Internal');
    expect(internal!.textContent).toContain('Fetched');
    expect(external!.textContent).toContain('External');
    expect(external!.textContent).not.toContain('×');

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('list', { name: 'Outgoing links of /' })).toBeNull();
  });
});
