import type { TreeNodeDto } from '../api/types';

export function node(pageId: string, children: TreeNodeDto[] = [], overrides: Partial<TreeNodeDto> = {}): TreeNodeDto {
  return {
    pageId,
    url: `https://example.com/${pageId === 'root' ? '' : pageId}`,
    depth: 0,
    fetchStatus: 'Fetched',
    httpStatusCode: 200,
    domainLinkRatio: 0.5,
    outgoingLinkCount: 4,
    internalLinkCount: 2,
    errorMessage: null,
    children,
    ...overrides,
  };
}

/**
 * root
 * ├── a
 * │   ├── a1
 * │   └── a2
 * └── b
 *     └── b1
 *         └── b1x
 */
export function sampleTree(): TreeNodeDto {
  return node('root', [
    node('a', [node('a1', [], { depth: 2 }), node('a2', [], { depth: 2 })], { depth: 1 }),
    node('b', [node('b1', [node('b1x', [], { depth: 3 })], { depth: 2 })], { depth: 1 }),
  ]);
}
