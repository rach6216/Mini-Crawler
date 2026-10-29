using System.ComponentModel.DataAnnotations;

namespace Crawler.Api.Infrastructure;

/// <summary>Browser origins allowed to call the API (the Vite dev server and the nginx-served UI).</summary>
public sealed class FrontendCorsOptions
{
    public const string SectionName = "Cors";
    public const string PolicyName = "frontend";

    [Required, MinLength(1)] public string[] AllowedOrigins { get; set; } = [];
}
