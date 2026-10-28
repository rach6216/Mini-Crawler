using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

// Expected values come from docs/test-plan.md section 2 (R1–R10).
public sealed class DomainLinkRatioTests
{
    private const string StartingHost = "example.com";

    private static LinkMetrics Calculate(params string[] links) =>
        DomainLinkRatio.Calculate(links.Select(l => new Uri(l)), StartingHost);

    [Fact] // R1
    public void No_links_yields_zero()
    {
        var metrics = Calculate();

        Assert.Equal(new LinkMetrics(0, 0, 0d), metrics);
    }

    [Fact] // R2
    public void All_internal_yields_one()
    {
        var metrics = Calculate("https://example.com/a", "https://example.com/b");

        Assert.Equal(new LinkMetrics(2, 2, 1d), metrics);
    }

    [Fact] // R3
    public void All_external_yields_zero()
    {
        var metrics = Calculate("https://other.org/a", "https://other.org/b");

        Assert.Equal(new LinkMetrics(2, 0, 0d), metrics);
    }

    [Fact] // R4
    public void Mixed_links_yield_fraction()
    {
        var metrics = Calculate("https://example.com/a", "https://other.org/b", "https://example.com/c", "https://x.io/");

        Assert.Equal(new LinkMetrics(4, 2, 0.5d), metrics);
    }

    [Fact] // R5
    public void Duplicate_occurrences_count_separately()
    {
        var metrics = Calculate("https://example.com/a", "https://example.com/a", "https://example.com/a", "https://other.org/b");

        Assert.Equal(new LinkMetrics(4, 3, 0.75d), metrics);
    }

    [Fact] // R6
    public void Subdomains_are_external()
    {
        var metrics = Calculate("https://www.example.com/a", "https://blog.example.com/b", "https://example.com/c");

        Assert.Equal(3, metrics.TotalLinks);
        Assert.Equal(1, metrics.InternalLinks);
        Assert.Equal(1.0 / 3.0, metrics.Ratio);
    }

    [Fact] // R7
    public void Scheme_does_not_affect_host_match()
    {
        var metrics = Calculate("http://example.com/a", "https://example.com/b");

        Assert.Equal(new LinkMetrics(2, 2, 1d), metrics);
    }

    [Fact] // R9
    public void Host_comparison_is_case_insensitive()
    {
        var metrics = DomainLinkRatio.Calculate([new Uri("https://EXAMPLE.com/a")], "Example.COM");

        Assert.Equal(new LinkMetrics(1, 1, 1d), metrics);
    }

    [Fact] // R10
    public void Port_is_ignored_for_host_match()
    {
        var metrics = Calculate("https://example.com:8443/a");

        Assert.Equal(new LinkMetrics(1, 1, 1d), metrics);
    }

    [Fact]
    public void Unsupported_schemes_are_ignored_defensively()
    {
        var metrics = Calculate("mailto:a@example.com", "ftp://example.com/file", "https://example.com/a");

        Assert.Equal(new LinkMetrics(1, 1, 1d), metrics);
    }
}
