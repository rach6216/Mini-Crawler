using System.Text.Json.Serialization;
using Crawler.Api.Endpoints;
using Crawler.Api.Infrastructure;
using Crawler.Api.Services;
using Crawler.Core.Infrastructure;
using Crawler.Core.Infrastructure.Outbox;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = true;
        o.SingleLine = true;
    });
}
else
{
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Registered before the outbox publisher so the schema exists before any background work starts.
builder.Services.AddHostedService<DatabaseMigrationService>();

builder.Services.AddCrawlerCore(builder.Configuration);

// Tests that do not exercise publishing switch the background publisher off; writers still need the signal.
if (builder.Configuration.GetValue("Outbox:Enabled", true))
{
    builder.Services.AddOutboxPublisher();
}
else
{
    builder.Services.AddOutboxSignal();
}

builder.Services.AddScoped<JobCommandService>();
builder.Services.AddScoped<JobQueryService>();
builder.Services.AddSingleton<CreateJobValidator>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
});
builder.Services.AddProblemDetails();

const string FrontendCorsPolicy = "frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5173", "http://localhost:3000"];
builder.Services.AddCors(o => o.AddPolicy(FrontendCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .WithMethods("GET", "POST")
    .WithExposedHeaders("Location")));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors(FrontendCorsPolicy);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
app.MapJobEndpoints();

app.Run();

public partial class Program;
