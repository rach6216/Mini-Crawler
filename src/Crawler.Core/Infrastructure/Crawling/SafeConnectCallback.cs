using System.Net;
using System.Net.Sockets;
using Crawler.Core.Application.Options;
using Crawler.Core.Domain;
using Microsoft.Extensions.Options;

namespace Crawler.Core.Infrastructure.Crawling;

/// <summary>
/// <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the host itself, validates every
/// resolved address and connects only to a validated address. Validating here rather than before the
/// request closes the DNS-rebinding window: the address that was checked is the address that is used.
/// TLS/SNI is layered on top of the returned stream by the handler.
/// </summary>
public sealed class SafeConnectCallback(IDnsResolver resolver, IOptions<CrawlerOptions> options)
{
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new BlockedDestinationException(host, "host did not resolve to any address");
        }

        // One bad address rejects the host: an attacker controlling DNS could otherwise mix a public and a private record.
        if (!UrlSafety.IsTrustedHost(host, options.Value.Security.TrustedHosts))
        {
            var unsafeAddress = addresses.FirstOrDefault(a => !UrlSafety.IsPubliclyRoutable(a));
            if (unsafeAddress is not null)
            {
                // The resolved address stays out of the exception (it ends up in the page's error text) so
                // the crawler cannot be used to map internal DNS names to addresses.
                throw new BlockedDestinationException(host, "resolves to a non-public address");
            }
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not connect to {host}:{port}", lastError);
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        // IP literals need no DNS; brackets come from IPv6 URL syntax.
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            return [literal];
        }

        return await resolver.ResolveAsync(host, cancellationToken);
    }
}
