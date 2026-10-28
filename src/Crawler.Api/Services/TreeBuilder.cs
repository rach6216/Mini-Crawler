using Crawler.Core.Application.Contracts;
using Crawler.Core.Domain;

namespace Crawler.Api.Services;

/// <summary>
/// Builds the first-discovery spanning tree from <see cref="CrawlPage.ParentPageId"/>. Iterative and
/// guarded by a visited set, so deep chains cannot overflow the stack and corrupt parent links
/// (cycles, self references, orphans) cannot produce duplicate or infinite nodes.
/// </summary>
public static class TreeBuilder
{
    public static TreeNodeDto? Build(IReadOnlyCollection<CrawlPage> pages)
    {
        var root = pages
            .Where(p => p.ParentPageId is null && p.Depth == 0)
            .OrderBy(p => p.DiscoveryOrder)
            .FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        var childrenByParent = pages
            .Where(p => p.ParentPageId is not null)
            .GroupBy(p => p.ParentPageId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.DiscoveryOrder).ThenBy(p => p.Id).ToList());

        // Breadth-first walk assigning each page to the first parent that reaches it.
        var visitOrder = new List<CrawlPage>(pages.Count);
        var treeChildren = new Dictionary<Guid, List<Guid>>();
        var visited = new HashSet<Guid> { root.Id };
        var queue = new Queue<CrawlPage>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var page = queue.Dequeue();
            visitOrder.Add(page);
            var accepted = new List<Guid>();
            if (childrenByParent.TryGetValue(page.Id, out var children))
            {
                foreach (var child in children)
                {
                    if (visited.Add(child.Id))
                    {
                        accepted.Add(child.Id);
                        queue.Enqueue(child);
                    }
                }
            }

            treeChildren[page.Id] = accepted;
        }

        // Reverse BFS order guarantees every child node is built before its parent.
        var built = new Dictionary<Guid, TreeNodeDto>(visitOrder.Count);
        for (var i = visitOrder.Count - 1; i >= 0; i--)
        {
            var page = visitOrder[i];
            var children = treeChildren[page.Id].Select(id => built[id]).ToList();
            built[page.Id] = ToNode(page, children);
        }

        return built[root.Id];
    }

    private static TreeNodeDto ToNode(CrawlPage page, IReadOnlyList<TreeNodeDto> children) =>
        new(
            page.Id,
            page.NormalizedUrl,
            page.Depth,
            page.FetchStatus,
            page.HttpStatusCode,
            page.DomainLinkRatio,
            page.OutgoingLinkCount,
            page.InternalLinkCount,
            page.ErrorMessage,
            children);
}
