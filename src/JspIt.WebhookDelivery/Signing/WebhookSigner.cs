using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JspIt.WebhookDelivery;

/// <summary>Computes webhook request signatures. Used by the dispatcher and by <see cref="WebhookSignatureVerifier"/>.</summary>
public static class WebhookSigner
{
    private const string V1Prefix = "v1=";
    private const string LegacyPrefix = "sha256=";

    /// <summary>Headers produced by signing one request.</summary>
    /// <param name="HeaderName">Signature header name.</param>
    /// <param name="HeaderValue">Signature header value.</param>
    /// <param name="Timestamp">Unix-seconds timestamp to send in <see cref="WebhookHeaders.Timestamp"/>, or null for schemes without one.</param>
    public readonly record struct SignatureHeaders(string HeaderName, string HeaderValue, string? Timestamp);

    /// <summary>
    /// Signs a request with every key active at <paramref name="now"/> (default scheme) or with the first
    /// active key (body-only scheme).
    /// </summary>
    /// <exception cref="InvalidOperationException">No configured key is active at <paramref name="now"/>.</exception>
    public static SignatureHeaders Sign(
        WebhookSignatureScheme scheme,
        IReadOnlyList<WebhookSigningKey> keys,
        DateTimeOffset now,
        string idempotencyKey,
        string eventType,
        ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var active = keys.Where(k => k.IsActiveAt(now)).ToList();
        if (active.Count == 0)
        {
            throw new InvalidOperationException("No signing key is active at the current time.");
        }

        switch (scheme)
        {
            case WebhookSignatureScheme.TimestampedHmacSha256V1:
            {
                var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var parts = new string[active.Count];
                for (var i = 0; i < active.Count; i++)
                {
                    parts[i] = V1Prefix + ComputeV1Hex(active[i].Secret, timestamp, idempotencyKey, eventType, body);
                }

                return new SignatureHeaders(WebhookHeaders.Signature, string.Join(", ", parts), timestamp);
            }

            case WebhookSignatureScheme.BodyHmacSha256:
                return new SignatureHeaders(WebhookHeaders.LegacySignature, LegacyPrefix + ComputeBodyHex(active[0].Secret, body), null);

            default:
                throw new ArgumentOutOfRangeException(nameof(scheme));
        }
    }

    internal static string ComputeV1Hex(ReadOnlySpan<byte> secret, string timestamp, string idempotencyKey, string eventType, ReadOnlySpan<byte> body)
    {
        var header = Encoding.UTF8.GetBytes($"v1\n{timestamp}\n{idempotencyKey}\n{eventType}\n");
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        hmac.AppendData(header);
        hmac.AppendData(body);
        Span<byte> mac = stackalloc byte[32];
        hmac.GetHashAndReset(mac);
        return Convert.ToHexStringLower(mac);
    }

    internal static string ComputeBodyHex(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(secret, body, mac);
        return Convert.ToHexStringLower(mac);
    }

    internal static IEnumerable<string> ParseV1Signatures(string headerValue)
    {
        foreach (var part in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(V1Prefix, StringComparison.Ordinal))
            {
                yield return part[V1Prefix.Length..];
            }
        }
    }

    internal static string? ParseLegacySignature(string headerValue) =>
        headerValue.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? headerValue[LegacyPrefix.Length..].Trim() : null;
}
