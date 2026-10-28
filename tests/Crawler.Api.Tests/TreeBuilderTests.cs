using Crawler.Api.Services;
using Crawler.Core.Application.Contracts;
using Crawler.Core.Domain;

namespace Crawler.Api.Tests;

public sealed class TreeBuilderTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    [Fact]
    public void Returns_null_when_there_are_no_pages()
    {
        Assert.Null(TreeBuilder.Build([]));
    }

    [Fact]
    public void A9_builds_nested_tree_with_children_in_discovery_order()
    {
        var root = Root("https://example.com/");
        var a = Child(root, "https://example.com/a", 1, discoveryOrder: 1);
        var b = Child(root, "https://example.com/b", 1, discoveryOrder: 2);
        var c = Child(a, "https://example.com/c", 2, discoveryOrder: 3);

        // Input order deliberately differs from discovery order.
        var tree = TreeBuilder.Build([c, b, root, a]);

        Assert.NotNull(tree);
        Assert.Equal(root.Id, tree.PageId);
        Assert.Equal([a.Id, b.Id], tree.Children.Select(n => n.PageId));
        Assert.Equal([c.Id], tree.Children[0].Children.Select(n => n.PageId));
        Assert.Empty(tree.Children[1].Children);
        Assert.Equal(4, Flatten(tree).Count);
        Assert.Equal(4, Flatten(tree).Select(n => n.PageId).Distinct().Count());
    }

    [Fact]
    public void Maps_page_fields_onto_the_node()
    {
        var root = Root("https://example.com/");
        root.MarkFetched(200, new LinkMetrics(4, 3, 0.75), DateTimeOffset.UtcNow);
        var failed = Child(root, "https://example.com/broken", 1, discoveryOrder: 1);
        failed.MarkFailed(500, "HTTP 500", DateTimeOffset.UtcNow);

        var tree = TreeBuilder.Build([root, failed])!;

        Assert.Equal("https://example.com/", tree.Url);
        Assert.Equal(0, tree.Depth);
        Assert.Equal(PageFetchStatus.Fetched, tree.FetchStatus);
        Assert.Equal(200, tree.HttpStatusCode);
        Assert.Equal(0.75, tree.DomainLinkRatio);
        Assert.Equal(4, tree.OutgoingLinkCount);
        Assert.Equal(3, tree.InternalLinkCount);
        Assert.Null(tree.ErrorMessage);

        var child = Assert.Single(tree.Children);
        Assert.Equal(PageFetchStatus.Failed, child.FetchStatus);
        Assert.Equal(500, child.HttpStatusCode);
        Assert.Equal("HTTP 500", child.ErrorMessage);
    }

    [Fact]
    public void Corrupt_parent_links_cannot_create_cycles_or_duplicates()
    {
        var root = Root("https://example.com/");
        var a = Child(root, "https://example.com/a", 1, discoveryOrder: 1);
        var selfLoop = Child(root, "https://example.com/self", 1, discoveryOrder: 2);
        selfLoop.ParentPageId = selfLoop.Id;
        var p = Child(root, "https://example.com/p", 2, discoveryOrder: 3);
        var q = Child(root, "https://example.com/q", 2, discoveryOrder: 4);
        p.ParentPageId = q.Id;
        q.ParentPageId = p.Id;

        var tree = TreeBuilder.Build([root, a, selfLoop, p, q])!;

        var nodes = Flatten(tree);
        Assert.Equal([root.Id, a.Id], nodes.Select(n => n.PageId));
    }

    [Fact]
    public void Deep_chains_are_built_without_recursion()
    {
        var pages = new List<CrawlPage> { Root("https://example.com/") };
        for (var i = 1; i <= 5000; i++)
        {
            pages.Add(Child(pages[^1], $"https://example.com/{i}", i, discoveryOrder: i));
        }

        var tree = TreeBuilder.Build(pages)!;

        var depth = 0;
        for (var node = tree; node.Children.Count > 0; node = node.Children[0])
        {
            depth++;
        }

        Assert.Equal(5000, depth);
    }

    private static CrawlPage Root(string url) => CrawlPage.CreateRoot(JobId, new Uri(url));

    private static CrawlPage Child(CrawlPage parent, string url, int depth, int discoveryOrder) =>
        CrawlPage.CreateChild(JobId, new Uri(url), depth, parent.Id, discoveryOrder);

    private static List<TreeNodeDto> Flatten(TreeNodeDto root)
    {
        var result = new List<TreeNodeDto>();
        var queue = new Queue<TreeNodeDto>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            result.Add(node);
            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }
}
