using Crawler.Core.Infrastructure;
using Crawler.Core.Infrastructure.Crawling;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using Crawler.Worker.Recovery;

namespace Crawler.Worker;

public static class WorkerServiceCollectionExtensions
{
    /// <summary>Everything the worker needs, including the consumer and lease sweeper hosted services.</summary>
    public static IServiceCollection AddCrawlerWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCrawlerWorkerServices(configuration);
        services.AddHostedService<JobConsumer>();
        services.AddHostedService(provider => provider.GetRequiredService<LeaseRecoveryService>());
        return services;
    }

    /// <summary>Worker services without hosted services, so tests can drive handler and sweeper directly.</summary>
    public static IServiceCollection AddCrawlerWorkerServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCrawlerCore(configuration);
        services.AddCrawling(configuration);

        services.AddSingleton(WorkerIdentity.CreateForProcess());
        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<InFlightJobs>();
        services.AddScoped<ICrawlEngine, CrawlEngine>();
        services.AddSingleton<IJobMessageHandler, JobMessageHandler>();
        services.AddSingleton<LeaseRecoveryService>();
        return services;
    }
}
