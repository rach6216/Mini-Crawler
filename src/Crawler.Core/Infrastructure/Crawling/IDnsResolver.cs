using System.Net;

namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>Seam over DNS so the SSRF policy can be tested with hosts that resolve to private addresses.</summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}
