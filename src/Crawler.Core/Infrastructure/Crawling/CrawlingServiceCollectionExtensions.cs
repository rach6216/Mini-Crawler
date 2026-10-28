using System.Net;
using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Infrastructure.Crawling;

public static class CrawlingServiceCollectionExtensions
{
    public const string HttpClientName = "crawler";

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers the SSRF-safe HTTP fetcher, the robots.txt policy provider and the link extractor. Options and the clock come from
    /// <see cref="CoreServiceCollectionExtensions.AddCrawlerCore"/>.
    /// </summary>
    public static IServiceCollection AddCrawling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<IRetryDelay, TimeProviderRetryDelay>();
        services.TryAddSingleton<SafeConnectCallback>();
        services.TryAddSingleton<ILinkExtractor, AngleSharpLinkExtractor>();
        services.TryAddSingleton<IPageFetcher, HttpPageFetcher>();
        services.TryAddSingleton<IRobotsPolicyProvider, RobotsPolicyProvider>();

        services.AddHttpClient(HttpClientName, client =>
            {
                // Per-attempt timeouts are enforced by the fetcher so that they also cover the body read.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var http = provider.GetRequiredService<IOptions<CrawlerOptions>>().Value.Http;
                var connect = provider.GetRequiredService<SafeConnectCallback>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    // A proxy would receive the connection instead of the validated address.
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(http.TimeoutSeconds),
                    // Abandoned bodies (non-HTML, oversized) are not drained; the connection is dropped instead.
                    MaxResponseDrainSize = 0,
                    PooledConnectionLifetime = PooledConnectionLifetime,
                    ConnectCallback = connect.ConnectAsync,
                };
            });

        return services;
    }
}
