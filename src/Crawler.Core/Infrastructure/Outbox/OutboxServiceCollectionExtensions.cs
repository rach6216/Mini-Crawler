using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crawler.Core.Infrastructure.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Wake-up signal used by writers of outbox rows. Safe to call more than once.</summary>
    public static IServiceCollection AddOutboxSignal(this IServiceCollection services)
    {
        services.TryAddSingleton<OutboxSignal>();
        return services;
    }

    /// <summary>Signal plus the hosted service that drains the outbox. Requires <c>AddCrawlerCore</c>.</summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services)
    {
        services.AddOutboxSignal();
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }
}
