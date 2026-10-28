namespace Crawler.Core.Domain;

/// <summary>
/// The robots.txt group that applies to one product token (RFC 9309): parsing, group selection and
/// longest-match evaluation. Pure; fetching and failure policy live in the infrastructure layer.
/// </summary>
public sealed class RobotsRules
{
    public const string WildcardAgent = "*";

    private const char ByteOrderMark = (char)0xFEFF;

    private readonly IReadOnlyList<RobotsRule> _rules;

    private RobotsRules(string? group, IReadOnlyList<RobotsRule> rules)
    {
        Group = group;
        _rules = rules;
    }

    /// <summary>No applicable group: every path is allowed.</summary>
    public static RobotsRules AllowAll { get; } = new(null, []);

    /// <summary>The selected group: the product token, <c>*</c>, or <c>null</c> when no group applies.</summary>
    public string? Group { get; }

    public IReadOnlyList<RobotsRule> Rules => _rules;

    public int RuleCount => _rules.Count;

    /// <summary>
    /// Selects every group whose <c>User-agent</c> value contains <paramref name="productToken"/>
    /// (case-insensitive), falling back to the <c>*</c> groups; groups with the same selection are merged.
    /// </summary>
    public static RobotsRules Parse(string? content, string productToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productToken);
        if (string.IsNullOrEmpty(content))
        {
            return AllowAll;
        }

        var tokenRules = new List<RobotsRule>();
        var wildcardRules = new List<RobotsRule>();
        var tokenGroupSeen = false;
        var wildcardGroupSeen = false;

        // Agents of the group being read; a rule line closes the agent list so the next User-agent starts a new group.
        var agents = new List<string>();
        var inRules = false;

        foreach (var rawLine in content.TrimStart(ByteOrderMark).Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (inRules)
                {
                    agents.Clear();
                    inRules = false;
                }

                agents.Add(value);
                if (Matches(value, productToken))
                {
                    tokenGroupSeen = true;
                }
                else if (value == WildcardAgent)
                {
                    wildcardGroupSeen = true;
                }

                continue;
            }

            var allow = key.Equals("allow", StringComparison.OrdinalIgnoreCase);
            if (!allow && !key.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                // Sitemap, Crawl-delay and unknown records neither carry rules nor end a group.
                continue;
            }

            inRules = true;
            if (agents.Count == 0 || value.Length == 0)
            {
                // Rules outside any group are ignored; an empty pattern matches nothing (empty Disallow = allow all).
                continue;
            }

            var rule = new RobotsRule(allow, value);
            if (agents.Exists(agent => Matches(agent, productToken)))
            {
                tokenRules.Add(rule);
            }
            else if (agents.Contains(WildcardAgent))
            {
                wildcardRules.Add(rule);
            }
        }

        if (tokenGroupSeen)
        {
            return new RobotsRules(productToken, tokenRules);
        }

        return wildcardGroupSeen ? new RobotsRules(WildcardAgent, wildcardRules) : AllowAll;
    }

    /// <summary>
    /// Longest matching pattern wins; on equal length <c>Allow</c> wins; no match means allowed.
    /// <paramref name="pathAndQuery"/> is compared as-is (no percent-decoding).
    /// </summary>
    public bool IsAllowed(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        RobotsRule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(pathAndQuery))
            {
                continue;
            }

            if (best is null
                || rule.Pattern.Length > best.Pattern.Length
                || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static bool Matches(string agentValue, string productToken) =>
        agentValue.Contains(productToken, StringComparison.OrdinalIgnoreCase);

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }
}

/// <summary>One <c>Allow</c>/<c>Disallow</c> pattern: <c>*</c> matches any sequence, a trailing <c>$</c> anchors the end.</summary>
public sealed record RobotsRule(bool Allow, string Pattern)
{
    public bool Matches(string path)
    {
        var anchored = Pattern.EndsWith('$');
        var pattern = anchored ? Pattern[..^1] : Pattern;

        // Glob matching with single-star backtracking; an unanchored pattern only has to match a prefix.
        int p = 0, s = 0, star = -1, starMatch = 0;
        while (true)
        {
            if (p == pattern.Length && (!anchored || s == path.Length))
            {
                return true;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starMatch = s;
                continue;
            }

            if (p < pattern.Length && s < path.Length && pattern[p] == path[s])
            {
                p++;
                s++;
                continue;
            }

            if (star >= 0 && starMatch < path.Length)
            {
                p = star + 1;
                s = ++starMatch;
                continue;
            }

            return false;
        }
    }
}
