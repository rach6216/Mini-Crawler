using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Infrastructure;

/// <summary>Page-size defaults and caps for the list endpoints (`Api:Paging`).</summary>
public sealed class PagingOptions
{
    public const string SectionName = "Api:Paging";

    [Range(1, 1000)] public int DefaultPageSize { get; set; } = 20;
    [Range(1, 1000)] public int MaxPageSize { get; set; } = 100;
    [Range(1, 5000)] public int DefaultLinksPageSize { get; set; } = 100;
    [Range(1, 5000)] public int MaxLinksPageSize { get; set; } = 500;
}

public sealed class PagingOptionsValidator : IValidateOptions<PagingOptions>
{
    public ValidateOptionsResult Validate(string? name, PagingOptions options)
    {
        var failures = new List<string>();
        if (options.DefaultPageSize > options.MaxPageSize)
        {
            failures.Add("Api:Paging:DefaultPageSize must not exceed MaxPageSize.");
        }

        if (options.DefaultLinksPageSize > options.MaxLinksPageSize)
        {
            failures.Add("Api:Paging:DefaultLinksPageSize must not exceed MaxLinksPageSize.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
