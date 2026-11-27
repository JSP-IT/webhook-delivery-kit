using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace JspIt.WebhookDelivery.Http;

/// <summary>Raised from the connect callback when every resolved address is forbidden by the destination's network policy.</summary>
internal sealed class DestinationBlockedException : Exception
{
    public DestinationBlockedException(string message) : base(message) { }
}

/// <summary>
/// Builds one hardened <see cref="HttpMessageInvoker"/> per destination. A handler per destination keeps
/// pooled connections from being shared between destinations with different network policies.
/// </summary>
internal static class DestinationHttpClientFactory
{
    public static HttpMessageInvoker Create(WebhookDestination destination, int maxConnectionsPerServer)
    {
        var allowLoopback = destination.AllowLoopback;
        var allowedNetworks = destination.AllowedPrivateNetworks.ToArray();

        var handler = new SocketsHttpHandler
        {
            // Redirects are never followed: a redirect could point at an internal address or a different host,
            // and would re-send the signed payload there. A 3xx is a permanent failure.
            AllowAutoRedirect = false,
            UseCookies = false,
            // No proxy: a proxy resolves DNS itself, which would bypass the address checks below.
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = destination.Timeout < TimeSpan.FromSeconds(10) ? destination.Timeout : TimeSpan.FromSeconds(10),
            // Recycle connections so DNS changes are picked up and re-validated.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = maxConnectionsPerServer,
            MaxResponseHeadersLength = 64, // KiB
            ResponseDrainTimeout = TimeSpan.FromSeconds(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                // TLS 1.2+ only. Certificate validation uses the platform defaults and cannot be disabled.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, allowLoopback, allowedNetworks, cancellationToken),
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        bool allowLoopback,
        IReadOnlyList<IPNetwork> allowedNetworks,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // Every resolved address must be allowed. A name that resolves to a mix of public and internal
        // addresses is treated as hostile (typical of DNS-rebinding setups) rather than silently filtered.
        foreach (var address in addresses)
        {
            if (!NetworkAddressPolicy.IsAllowed(address, allowLoopback, allowedNetworks))
            {
                throw new DestinationBlockedException(
                    $"Connection to {address} blocked by destination network policy (non-public address; configure AllowLoopback or AllowedPrivateNetworks to permit it).");
            }
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the validated IP itself (not the host name) so no second DNS lookup can occur.
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
