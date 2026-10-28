using Crawler.Core.Application.Abstractions;
using Crawler.Core.Application.Messaging;
using Crawler.Core.Application.Options;
using Crawler.Core.Infrastructure.Messaging;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Infrastructure;

public static class CoreServiceCollectionExtensions
{
    public const string PostgresConnectionName = "Postgres";

    /// <summary>Options, DbContext, job state store, RabbitMQ connection/publisher and health checks shared by API and Worker.</summary>
    public static IServiceCollection AddCrawlerCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CrawlerOptions>().Bind(configuration.GetSection(CrawlerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CrawlerOptions>, CrawlerOptionsValidator>();
        services.AddSingleton<IValidateOptions<CrawlerOptions>, CrawlerOptionsConsistencyValidator>();
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);

        var connectionString = configuration.GetConnectionString(PostgresConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{PostgresConnectionName}' is not configured.");
        services.AddDbContext<CrawlerDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IJobStateStore, JobStateStore>();

        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IJobMessagePublisher, RabbitMqJobMessagePublisher>();

        services.AddHealthChecks()
            .AddDbContextCheck<CrawlerDbContext>("postgres", tags: ["ready"])
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

        return services;
    }

    private static void TryAddSingleton<TService>(this IServiceCollection services, TService instance) where TService : class
    {
        if (services.All(d => d.ServiceType != typeof(TService)))
        {
            services.AddSingleton(instance);
        }
    }
}
