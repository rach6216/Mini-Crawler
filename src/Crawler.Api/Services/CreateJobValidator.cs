using Crawler.Core.Application.Contracts;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Services;

public sealed record ValidatedCreateJob(string InputUrl, Uri NormalizedStartUrl, int MaxDepth, int MaxPages);

/// <summary>
/// Syntactic validation of a create request. DNS-based SSRF checks happen in the worker's connect
/// callback, where the resolved addresses are known.
/// </summary>
public sealed class CreateJobValidator(IOptions<CrawlerOptions> options)
{
    public const string UrlField = "url";
    public const string MaxDepthField = "maxDepth";
    public const string MaxPagesField = "maxPages";

    public bool TryValidate(CreateJobRequest? request, out ValidatedCreateJob? job, out Dictionary<string, string[]> errors)
    {
        var limits = options.Value.Limits;
        var trustedHosts = options.Value.Security.TrustedHosts;
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        job = null;

        Uri? normalized = null;
        if (string.IsNullOrWhiteSpace(request?.Url))
        {
            errors[UrlField] = ["The url field is required."];
        }
        else if (!UrlNormalizer.TryNormalize(request.Url, null, out normalized))
        {
            errors[UrlField] = ["The url must be an absolute http or https URL."];
        }
        else if (!UrlSafety.IsHostAllowedWithoutDns(normalized.Host, trustedHosts))
        {
            errors[UrlField] = ["The url host must not be a loopback, private or link-local address."];
        }

        var maxDepth = request?.MaxDepth ?? limits.DefaultMaxDepth;
        if (maxDepth < 0 || maxDepth > limits.MaxAllowedDepth)
        {
            errors[MaxDepthField] = [$"maxDepth must be between 0 and {limits.MaxAllowedDepth}."];
        }

        var maxPages = request?.MaxPages ?? limits.DefaultMaxPages;
        if (maxPages < 1 || maxPages > limits.MaxAllowedPages)
        {
            errors[MaxPagesField] = [$"maxPages must be between 1 and {limits.MaxAllowedPages}."];
        }

        if (errors.Count > 0 || normalized is null)
        {
            return false;
        }

        job = new ValidatedCreateJob(request!.Url!.Trim(), normalized, maxDepth, maxPages);
        return true;
    }
}
