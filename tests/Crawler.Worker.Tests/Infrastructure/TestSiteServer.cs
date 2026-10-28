using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Crawler.Worker.Tests.Infrastructure;

/// <summary>
/// Kestrel on 127.0.0.1 with a free port: serves tests/fixtures/site plus programmable routes for
/// retry, redirect, content-type, size and timeout scenarios. Counts requests per path.
/// </summary>
public sealed class TestSiteServer : IAsyncDisposable
{
    public const long BodyLimit = 4096;

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

    private TestSiteServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; private set; }

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    public int Hits(string path) => _hits.GetValueOrDefault(path);

    public int TotalHits => _hits.Values.Sum();

    /// <summary>When set, answers <c>/robots.txt</c> instead of the fixture file (e.g. a 503 or 404).</summary>
    public Func<IResult>? RobotsOverride { get; set; }

    public static async Task<TestSiteServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        var server = new TestSiteServer(app, 0);
        server.Configure(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.Port = new Uri(address).Port;
        return server;
    }

    private void Configure(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            _hits.AddOrUpdate(context.Request.Path.Value ?? "/", 1, (_, count) => count + 1);
            await next(context);
        });

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/robots.txt" && RobotsOverride is { } robots)
            {
                await robots().ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        var site = new PhysicalFileProvider(FixtureSiteFetcher.SiteRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = site });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = site });

        app.MapGet("/flaky", (HttpContext context) => Hits("/flaky") <= 2
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Html("<a href=\"/a.html\">recovered</a>"));
        app.MapGet("/always-503", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/forbidden", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapGet("/slow", async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Html("too late");
        });

        app.MapGet("/redirect-same-host", () => Results.Redirect("/a.html", permanent: true));
        app.MapGet("/redirect-external", () => Results.Redirect($"http://localhost:{Port}/never"));
        app.MapGet("/redirect-loop", () => Results.Redirect("/redirect-loop"));
        app.MapGet("/redirect-mailto", () => Results.Redirect("mailto:x@example.org"));
        app.MapGet("/never", () => Html("should never be requested"));

        app.MapGet("/pdf", async (HttpContext context) =>
        {
            // Headers go out immediately; the body never completes, so any attempt to read it would time out.
            context.Response.ContentType = "application/pdf";
            await context.Response.Body.WriteAsync("%PDF-1.7\n"u8.ToArray(), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        app.MapGet("/big", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            var chunk = Encoding.ASCII.GetBytes(new string('x', 1024));
            for (var written = 0L; written <= BodyLimit; written += chunk.Length)
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            }
        });
        app.MapGet("/big-declared", async (HttpContext context) =>
        {
            var body = Encoding.ASCII.GetBytes(new string('x', (int)BodyLimit + 1));
            context.Response.ContentType = "text/html";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        });
        app.MapGet("/latin1", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=iso-8859-1";
            await context.Response.Body.WriteAsync(Encoding.Latin1.GetBytes("<p>café</p>"), context.RequestAborted);
        });
        app.MapGet("/echo-headers", (HttpContext context) => Html(
            $"<p id=\"ua\">{context.Request.Headers.UserAgent}</p><p id=\"accept\">{context.Request.Headers.Accept}</p>" +
            $"<p id=\"cookie\">{context.Request.Headers.Cookie.Count}</p><p id=\"auth\">{context.Request.Headers.Authorization.Count}</p>"));
    }

    private static IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
