using System.Text.Json;
using System.Text.Json.Serialization;
using Crawler.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Api.Tests.Infrastructure;

public sealed class CrawlerApiFactory(CrawlerApiFactory.Settings settings) : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed class Settings(string postgresConnectionString, RabbitMqEndpoint rabbitMq)
    {
        public string PostgresConnectionString { get; } = postgresConnectionString;
        public RabbitMqEndpoint RabbitMq { get; set; } = rabbitMq;
        public bool OutboxEnabled { get; set; }
    }

    public Settings Configuration => settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: the development profile trusts localhost, which would defeat the SSRF tests.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", settings.PostgresConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Outbox:Enabled", settings.OutboxEnabled ? "true" : "false");
        builder.UseSetting("Crawler:Outbox:PollIntervalSeconds", "1");
        builder.UseSetting("RabbitMq:Host", settings.RabbitMq.Host);
        builder.UseSetting("RabbitMq:Port", settings.RabbitMq.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RabbitMq:UserName", settings.RabbitMq.UserName);
        builder.UseSetting("RabbitMq:Password", settings.RabbitMq.Password);
        builder.UseSetting("RabbitMq:VirtualHost", settings.RabbitMq.VirtualHost);
    }

    public async Task<T> WithDbAsync<T>(Func<CrawlerDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        return await action(db);
    }

    public Task WithDbAsync(Func<CrawlerDbContext, Task> action) =>
        WithDbAsync(async db =>
        {
            await action(db);
            return true;
        });
}
