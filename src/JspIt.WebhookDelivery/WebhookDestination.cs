using System.Net;

namespace JspIt.WebhookDelivery;

/// <summary>
/// An explicitly configured webhook endpoint. Messages are enqueued against a destination
/// <see cref="Id"/>; the URL is fixed by configuration and can never be supplied per message,
/// so the library cannot be used as an open relay to arbitrary URLs.
/// </summary>
public sealed class WebhookDestination
{
    /// <summary>Hard upper bound for <see cref="Timeout"/>.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Hard upper bound for <see cref="MaxPayloadBytes"/> (10 MiB).</summary>
    public const int MaxPayloadBytesLimit = 10 * 1024 * 1024;

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Transfer-Encoding", "Connection",
        "Keep-Alive", "Upgrade", "TE", "Trailer", "Expect", "Cookie", "Proxy-Authorization", "Proxy-Connection",
        "User-Agent", WebhookHeaders.IdempotencyKey, WebhookHeaders.LegacySignature,
    };

    /// <summary>Stable identifier used when enqueuing ([A-Za-z0-9._-], 1–100 chars).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Absolute endpoint URL. Must be <c>https</c> unless <see cref="AllowInsecureHttp"/> is set. User info
    /// (<c>user:pass@</c>) and fragments are rejected. Treat the URL as sensitive: only its scheme, host and
    /// port are ever logged.
    /// </summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// Keys used to sign each request. At least one key must be active at send time; unsigned delivery is not
    /// supported. All active keys sign each request in the default scheme, enabling zero-downtime rotation.
    /// </summary>
    public IReadOnlyList<WebhookSigningKey> SigningKeys { get; init; } = [];

    /// <summary>Signature format. Default <see cref="WebhookSignatureScheme.TimestampedHmacSha256V1"/>.</summary>
    public WebhookSignatureScheme SignatureScheme { get; init; } = WebhookSignatureScheme.TimestampedHmacSha256V1;

    /// <summary>Retry policy. Default <see cref="RetryPolicy.Default"/>.</summary>
    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;

    /// <summary>Per-attempt timeout covering connect, TLS, request and response headers (100 ms – 60 s). Default 10 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum payload size accepted at enqueue time (1 B – 10 MiB). Default 256 KiB.</summary>
    public int MaxPayloadBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Static headers added to every request, e.g. an <c>Authorization</c> header the receiver requires.
    /// Framing, content and webhook protocol headers are reserved and rejected. Header values are never logged.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Permit plain <c>http</c> URLs. Off by default; intended for local development and tests only.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Permit connecting to loopback addresses (127.0.0.0/8, ::1). Off by default; intended for local
    /// development, tests and sidecars only.
    /// </summary>
    public bool AllowLoopback { get; init; }

    /// <summary>
    /// Non-public networks this destination may connect to, e.g. <c>10.20.0.0/16</c> for an internal
    /// service. Empty by default, meaning only publicly routable addresses are allowed.
    /// </summary>
    public IReadOnlyList<IPNetwork> AllowedPrivateNetworks { get; init; } = [];

    /// <summary>Validates the destination, throwing <see cref="WebhookConfigurationException"/> when invalid.</summary>
    public void Validate()
    {
        Validation.Require(Validation.IsValidIdentifier(Id, 100), "Destination id must be 1-100 characters of [A-Za-z0-9._-].");
        var prefix = $"Destination '{Id}':";

        Validation.Require(Url is not null && Url.IsAbsoluteUri, $"{prefix} Url must be an absolute URI.");
        Validation.Require(Url!.Scheme == Uri.UriSchemeHttps || (Url.Scheme == Uri.UriSchemeHttp && AllowInsecureHttp),
            $"{prefix} Url must use https (http requires AllowInsecureHttp, for local development only).");
        Validation.Require(string.IsNullOrEmpty(Url.UserInfo), $"{prefix} Url must not contain user info; use Headers for credentials.");
        Validation.Require(string.IsNullOrEmpty(Url.Fragment), $"{prefix} Url must not contain a fragment.");
        Validation.Require(!string.IsNullOrEmpty(Url.IdnHost), $"{prefix} Url must have a host.");

        Validation.Require(SigningKeys is { Count: > 0 }, $"{prefix} at least one signing key is required.");
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in SigningKeys)
        {
            Validation.Require(key is not null, $"{prefix} signing keys must not be null.");
            Validation.Require(keyIds.Add(key!.KeyId), $"{prefix} duplicate signing key id '{key.KeyId}'.");
        }

        Validation.Require(Enum.IsDefined(SignatureScheme), $"{prefix} unknown signature scheme.");
        Validation.Require(RetryPolicy is not null, $"{prefix} RetryPolicy is required.");
        try
        {
            RetryPolicy!.Validate();
        }
        catch (WebhookConfigurationException ex)
        {
            throw new WebhookConfigurationException($"{prefix} {ex.Message}", ex);
        }

        Validation.Require(Timeout >= TimeSpan.FromMilliseconds(100) && Timeout <= MaxTimeout, $"{prefix} Timeout must be between 100 ms and 60 s.");
        Validation.Require(MaxPayloadBytes is >= 1 and <= MaxPayloadBytesLimit, $"{prefix} MaxPayloadBytes must be between 1 and {MaxPayloadBytesLimit}.");

        Validation.Require(Headers is not null, $"{prefix} Headers must not be null.");
        foreach (var (name, value) in Headers!)
        {
            Validation.Require(Validation.IsValidHeaderName(name), $"{prefix} invalid header name.");
            Validation.Require(!ReservedHeaders.Contains(name)
                    && !name.StartsWith(WebhookHeaders.ReservedPrefix, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase),
                $"{prefix} header '{name}' is reserved and cannot be configured.");
            Validation.Require(Validation.IsValidHeaderValue(value), $"{prefix} header '{name}' has an invalid value (control characters or too long).");
        }

        Validation.Require(AllowedPrivateNetworks is not null, $"{prefix} AllowedPrivateNetworks must not be null.");
        foreach (var network in AllowedPrivateNetworks!)
        {
            Validation.Require(network.PrefixLength >= (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 8 : 16),
                $"{prefix} AllowedPrivateNetworks entry {network} is too broad (minimum /8 for IPv4, /16 for IPv6).");
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"WebhookDestination({Id}, {Redaction.Endpoint(Url)})";
}
