using Microsoft.Extensions.Options;

namespace Crawler.Core.Application.Options;

/// <summary>
/// Cross-field rules the attribute validator cannot express. The lease is renewed by a heartbeat every
/// <see cref="WorkerOptions.LeaseRenewSeconds"/>; it must survive a couple of missed heartbeats (a slow
/// database round trip, a GC pause) or a healthy worker would lose its own job.
/// </summary>
public sealed class CrawlerOptionsConsistencyValidator : IValidateOptions<CrawlerOptions>
{
    public const int MinimumHeartbeatsPerLease = 3;

    public ValidateOptionsResult Validate(string? name, CrawlerOptions options)
    {
        var failures = new List<string>();

        if (options.Worker.LeaseSeconds < options.Worker.LeaseRenewSeconds * MinimumHeartbeatsPerLease)
        {
            failures.Add(
                $"Crawler:Worker:LeaseSeconds ({options.Worker.LeaseSeconds}) must be at least {MinimumHeartbeatsPerLease} × LeaseRenewSeconds " +
                $"({options.Worker.LeaseRenewSeconds}) so that a missed heartbeat does not lose the lease.");
        }

        if (options.Limits.DefaultMaxDepth > options.Limits.MaxAllowedDepth)
        {
            failures.Add("Crawler:Limits:DefaultMaxDepth must not exceed MaxAllowedDepth.");
        }

        if (options.Limits.DefaultMaxPages > options.Limits.MaxAllowedPages)
        {
            failures.Add("Crawler:Limits:DefaultMaxPages must not exceed MaxAllowedPages.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
