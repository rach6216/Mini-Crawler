using Crawler.Core.Infrastructure.Crawling;

namespace Crawler.Worker.Tests;

/// <summary>Test plan §3 (L1–L7) and N17 (base href), plus R8 through the extractor.</summary>
public sealed class AngleSharpLinkExtractorTests
{
    private static readonly Uri Document = new("https://example.com/dir/page.html");
    private readonly AngleSharpLinkExtractor _extractor = new();

    private string[] Extract(string html) => _extractor.Extract(html, Document).Select(u => u.AbsoluteUri).ToArray();

    [Fact]
    public void L1_returns_links_in_document_order()
    {
        var links = Extract("""<a href="a.html">1</a><a href="/b">2</a><a href="https://o.org/c">3</a>""");

        Assert.Equal(["https://example.com/dir/a.html", "https://example.com/b", "https://o.org/c"], links);
    }

    [Fact]
    public void L2_ignores_anchors_without_usable_href()
    {
        Assert.Empty(Extract("""<a>none</a><a href="">empty</a><a href="   ">blank</a>"""));
    }

    [Fact]
    public void L3_ignores_non_http_schemes()
    {
        Assert.Empty(Extract("""<a href="mailto:x@y">m</a><a href="tel:1">t</a><a href="javascript:f()">j</a>"""));
    }

    [Fact]
    public void L4_keeps_duplicate_occurrences_with_identical_normalized_url()
    {
        var links = Extract("""<a href="a.html#frag">1</a><a href="a.html">2</a>""");

        Assert.Equal(["https://example.com/dir/a.html", "https://example.com/dir/a.html"], links);
    }

    [Fact]
    public void L5_N17_base_href_wins_over_document_url()
    {
        var links = Extract("""<html><head><base href="https://cdn.example.com/assets/"></head><body><a href="a.html">x</a></body></html>""");

        Assert.Equal(["https://cdn.example.com/assets/a.html"], links);
    }

    [Fact]
    public void Relative_base_href_resolves_against_document_and_invalid_base_falls_back()
    {
        Assert.Equal(["https://example.com/other/a.html"], Extract("""<base href="/other/"><a href="a.html">x</a>"""));
        Assert.Equal(["https://example.com/dir/a.html"], Extract("""<base href="mailto:x@y"><a href="a.html">x</a>"""));
    }

    [Fact]
    public void L6_malformed_html_does_not_throw()
    {
        var links = Extract("""<div><a href="a.html"><p>unclosed""");

        Assert.Equal(["https://example.com/dir/a.html"], links);
    }

    [Fact]
    public void L7_attribute_names_are_case_insensitive()
    {
        Assert.Equal(["https://example.com/dir/A.html"], Extract("""<a HREF="A.html">x</a>"""));
    }

    [Fact]
    public void R8_extractor_output_feeds_the_ratio()
    {
        var links = _extractor.Extract(
            """<a href="x.html">1</a><a href="https://other.org">2</a><a href="mailto:a@b">3</a>""",
            new Uri("https://example.com/"));

        var metrics = Crawler.Core.Domain.DomainLinkRatio.Calculate(links, "example.com");
        Assert.Equal((2, 1, 0.5), (metrics.TotalLinks, metrics.InternalLinks, metrics.Ratio));
    }

    [Fact]
    public void Empty_document_yields_no_links()
    {
        Assert.Empty(Extract(string.Empty));
    }
}
