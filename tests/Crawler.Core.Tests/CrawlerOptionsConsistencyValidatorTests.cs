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

    [Fact]
    public void Connect_timeout_above_the_attempt_timeout_is_rejected()
    {
        var options = new CrawlerOptions();
        options.Http.TimeoutSeconds = 5;
        options.Http.ConnectTimeoutSeconds = 6;

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ConnectTimeoutSeconds"));
    }

    [Fact]
    public void Reconnect_initial_delay_above_the_maximum_is_rejected()
    {
        var options = new CrawlerOptions();
        options.Worker.ReconnectInitialDelaySeconds = 31;
        options.Worker.ReconnectMaxDelaySeconds = 30;

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ReconnectInitialDelaySeconds"));
    }

    [Fact]
    public void Outbox_poll_interval_above_the_backoff_cap_is_rejected()
    {
        var options = new CrawlerOptions();
        options.Outbox.PollIntervalSeconds = 60;
        options.Outbox.MaxBackoffSeconds = 30;

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("MaxBackoffSeconds"));
    }

    [Fact]
    public void Robots_limit_below_the_RFC_9309_minimum_fails_attribute_validation()
    {
        var options = new CrawlerOptions();
        options.Http.MaxRobotsBytes = 499 * 1024;

        var result = new CrawlerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("MaxRobotsBytes"));
    }
}
