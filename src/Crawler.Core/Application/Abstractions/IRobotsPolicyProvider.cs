using Crawler.Core.Domain;

namespace Crawler.Core.Application.Abstractions;

/// <summary>The robots.txt verdict for one job run: the applicable rules, or a reason why everything is disallowed.</summary>
public sealed class RobotsDecision
{
    public const string DisallowedReason = "Disallowed by robots.txt";

    private RobotsDecision(RobotsRules rules, string? unavailableReason)
    {
        Rules = rules;
        UnavailableReason = unavailableReason;
    }

    public static RobotsDecision AllowAll { get; } = new(RobotsRules.AllowAll, null);

    public RobotsRules Rules { get; }

    /// <summary>Set when robots.txt could not be retrieved (5xx, timeout, connection failure, blocked destination); every URL is then disallowed.</summary>
    public string? UnavailableReason { get; }

    public static RobotsDecision FromRules(RobotsRules rules) => new(rules ?? throw new ArgumentNullException(nameof(rules)), null);

    public static RobotsDecision Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new RobotsDecision(RobotsRules.AllowAll, reason);
    }

    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return UnavailableReason is null && Rules.IsAllowed(url.PathAndQuery);
    }

    /// <summary>The page error recorded for a URL that <see cref="IsAllowed"/> rejects.</summary>
    public string SkipReason => UnavailableReason is null ? DisallowedReason : $"robots.txt unavailable: {UnavailableReason}";
}

public interface IRobotsPolicyProvider
{
    /// <summary>
    /// Fetches and evaluates <c>/robots.txt</c> on the start URL's scheme, host and port. Never throws for
    /// HTTP-level problems (they become an allow-all or unavailable decision); only for cancellation.
    /// </summary>
    Task<RobotsDecision> GetAsync(Uri startUrl, CancellationToken cancellationToken);
}
