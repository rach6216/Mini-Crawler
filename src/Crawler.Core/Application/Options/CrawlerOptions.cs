using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Application.Options;

public sealed class CrawlerOptions
{
    public const string SectionName = "Crawler";

    [Required, ValidateObjectMembers] public LimitsOptions Limits { get; set; } = new();
    [Required, ValidateObjectMembers] public HttpOptions Http { get; set; } = new();
    [Required] public SecurityOptions Security { get; set; } = new();
    [Required, ValidateObjectMembers] public WorkerOptions Worker { get; set; } = new();
    [Required, ValidateObjectMembers] public OutboxOptions Outbox { get; set; } = new();
}

public sealed class LimitsOptions
{
    [Range(0, 10)] public int DefaultMaxDepth { get; set; } = 2;
    [Range(0, 10)] public int MaxAllowedDepth { get; set; } = 5;
    [Range(1, 10_000)] public int DefaultMaxPages { get; set; } = 200;
    [Range(1, 10_000)] public int MaxAllowedPages { get; set; } = 1000;
}

public sealed class HttpOptions
{
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 10;
    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;
    [Range(0, 60_000)] public int RetryBaseDelayMilliseconds { get; set; } = 500;
    [Range(0, 20)] public int MaxRedirects { get; set; } = 5;
    [Range(1024, 50 * 1024 * 1024)] public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    [Range(1, 32)] public int Concurrency { get; set; } = 4;
    [Required] public string UserAgent { get; set; } = "CrawlerAssignmentBot/1.0 (+https://github.com/example/crawler)";
    public bool RespectRobotsTxt { get; set; } = true;
}

public sealed class SecurityOptions
{
    /// <summary>Host names allowed to resolve to private/loopback addresses (fixture servers only).</summary>
    public string[] TrustedHosts { get; set; } = [];
}

public sealed class WorkerOptions
{
    [Range(1, 64)] public int Prefetch { get; set; } = 2;
    // A heartbeat renews the lease every LeaseRenewSeconds while the crawl runs, so the lease only has to
    // survive a few missed heartbeats (enforced by CrawlerOptionsConsistencyValidator). Recovery latency
    // after a crash is bounded by LeaseSeconds + RecoverySweepSeconds.
    [Range(5, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 1800)] public int LeaseRenewSeconds { get; set; } = 15;
    [Range(1, 50)] public int MaxMessageAttempts { get; set; } = 5;
    [Range(1, 3600)] public int RetryDelaySeconds { get; set; } = 10;
    [Range(1, 3600)] public int RecoverySweepSeconds { get; set; } = 30;
}

public sealed class OutboxOptions
{
    [Range(1, 300)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(1, 1000)] public int BatchSize { get; set; } = 50;
}

[OptionsValidator]
public sealed partial class CrawlerOptionsValidator : IValidateOptions<CrawlerOptions>;
