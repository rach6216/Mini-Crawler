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
    /// <summary>Per-attempt budget covering connect, headers and body read.</summary>
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 10;
    /// <summary>TCP/TLS connect budget; must not exceed <see cref="TimeoutSeconds"/>.</summary>
    [Range(1, 120)] public int ConnectTimeoutSeconds { get; set; } = 10;
    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;
    [Range(0, 60_000)] public int RetryBaseDelayMilliseconds { get; set; } = 500;
    /// <summary>Retry n waits RetryBaseDelayMilliseconds × RetryBackoffMultiplier^(n-1).</summary>
    [Range(1.0, 10.0)] public double RetryBackoffMultiplier { get; set; } = 2.0;
    [Range(0, 20)] public int MaxRedirects { get; set; } = 5;
    [Range(1024, 50 * 1024 * 1024)] public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>robots.txt bytes parsed; the rest is ignored. RFC 9309 §2.5 requires at least 500 KiB.</summary>
    [Range(500 * 1024, 10 * 1024 * 1024)] public int MaxRobotsBytes { get; set; } = 500 * 1024;
    /// <summary>Pooled connections are recycled after this long so DNS changes are picked up.</summary>
    [Range(1, 3600)] public int PooledConnectionLifetimeSeconds { get; set; } = 120;
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
    /// <summary>A job still Pending this long after creation is republished (its message was lost or never delivered).</summary>
    [Range(10, 86_400)] public int PendingRepublishAfterSeconds { get; set; } = 120;
    /// <summary>Minimum time between two republishes of the same job by one worker's sweeper.</summary>
    [Range(1, 86_400)] public int RepublishCooldownSeconds { get; set; } = 300;
    [Range(1, 1000)] public int RecoverySweepBatchSize { get; set; } = 50;
    /// <summary>Pause before a message that could not be settled is returned to the queue.</summary>
    [Range(0, 600)] public int RequeueDelaySeconds { get; set; } = 5;
    /// <summary>First pause before the consumer reopens a closed RabbitMQ channel.</summary>
    [Range(1, 300)] public int ReconnectInitialDelaySeconds { get; set; } = 1;
    /// <summary>Upper bound of the consumer reconnect backoff; at least <see cref="ReconnectInitialDelaySeconds"/>.</summary>
    [Range(1, 3600)] public int ReconnectMaxDelaySeconds { get; set; } = 30;
    /// <summary>Factor applied to the reconnect delay after each consecutive failure.</summary>
    [Range(1.0, 10.0)] public double ReconnectBackoffMultiplier { get; set; } = 2.0;
    /// <summary>Interval at which the worker re-checks that the database is reachable and migrated before consuming.</summary>
    [Range(1, 300)] public int DatabaseReadinessPollSeconds { get; set; } = 3;
}

public sealed class OutboxOptions
{
    [Range(1, 300)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(1, 1000)] public int BatchSize { get; set; } = 50;
    /// <summary>Factor applied to the poll interval after each consecutive failed publishing round.</summary>
    [Range(1.0, 10.0)] public double BackoffMultiplier { get; set; } = 2.0;
    /// <summary>Upper bound of the failure backoff; at least <see cref="PollIntervalSeconds"/>.</summary>
    [Range(1, 3600)] public int MaxBackoffSeconds { get; set; } = 30;
}

[OptionsValidator]
public sealed partial class CrawlerOptionsValidator : IValidateOptions<CrawlerOptions>;
