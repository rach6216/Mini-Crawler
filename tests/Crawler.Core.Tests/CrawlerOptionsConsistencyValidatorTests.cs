using Crawler.Core.Application.Options;

namespace Crawler.Core.Tests;

public sealed class CrawlerOptionsConsistencyValidatorTests
{
    private static readonly CrawlerOptionsConsistencyValidator Validator = new();

    [Fact]
    public void Defaults_are_consistent()
    {
        var result = Validator.Validate(null, new CrawlerOptions());

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void Lease_must_cover_several_heartbeats()
    {
        var options = new CrawlerOptions();
        options.Worker.LeaseRenewSeconds = 30;
        options.Worker.LeaseSeconds = 60; // only two heartbeats

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("LeaseSeconds"));
    }

    [Fact]
    public void Defaults_above_their_maximums_are_rejected()
    {
        var options = new CrawlerOptions();
        options.Limits.DefaultMaxDepth = 6;
        options.Limits.MaxAllowedDepth = 5;

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("DefaultMaxDepth"));
    }
}
