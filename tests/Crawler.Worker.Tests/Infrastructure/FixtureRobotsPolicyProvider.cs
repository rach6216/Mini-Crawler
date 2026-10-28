using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Crawler.Core.Infrastructure.Crawling;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>
/// Counterpart of <see cref="FixtureSiteFetcher"/>: evaluates <c>tests/fixtures/site/robots.txt</c> for any host
/// without network access, with the production parser, product token and RespectRobotsTxt switch.
/// </summary>
public sealed class FixtureRobotsPolicyProvider(IOptions<CrawlerOptions> options) : IRobotsPolicyProvider
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<RobotsDecision> GetAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var http = options.Value.Http;
        if (!http.RespectRobotsTxt)
        {
            return RobotsDecision.AllowAll;
        }

        var content = await File.ReadAllTextAsync(Path.Combine(FixtureSiteFetcher.SiteRoot, "robots.txt"), cancellationToken);
        return RobotsDecision.FromRules(RobotsRules.Parse(content, RobotsPolicyProvider.ProductToken(http.UserAgent)));
    }
}
