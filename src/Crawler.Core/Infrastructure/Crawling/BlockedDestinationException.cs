namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>Thrown from the connect callback when a destination fails the SSRF policy; never retried.</summary>
public sealed class BlockedDestinationException(string host, string reason)
    : Exception($"Destination '{host}' is blocked: {reason}")
{
    public string Host { get; } = host;
}
