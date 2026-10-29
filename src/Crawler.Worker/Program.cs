using Crawler.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(console =>
    {
        console.IncludeScopes = true;
        console.UseUtcTimestamp = true;
        console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}

builder.Services.AddCrawlerWorker(builder.Configuration);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
