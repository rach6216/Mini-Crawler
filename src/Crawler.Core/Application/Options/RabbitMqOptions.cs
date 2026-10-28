using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Application.Options;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required] public string Host { get; set; } = "localhost";
    [Range(1, 65535)] public int Port { get; set; } = 5672;
    [Required] public string UserName { get; set; } = "guest";
    [Required] public string Password { get; set; } = "guest";
    [Required] public string VirtualHost { get; set; } = "/";
}

[OptionsValidator]
public sealed partial class RabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>;
