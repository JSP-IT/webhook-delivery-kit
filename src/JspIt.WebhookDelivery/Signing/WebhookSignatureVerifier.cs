using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JspIt.WebhookDelivery;

/// <summary>Outcome of verifying a received webhook request.</summary>
public enum WebhookVerificationStatus
{
    /// <summary>A signature matched a configured key and the timestamp is within tolerance.</summary>
    Valid,

    /// <summary>A required header was missing.</summary>
    MissingHeader,

    /// <summary>A header was present but malformed.</summary>
    MalformedHeader,

    /// <summary>The timestamp is outside the configured tolerance (possible replay or clock skew).</summary>
    TimestampOutOfTolerance,

    /// <summary>No signature matched any configured key.</summary>
    SignatureMismatch,
}

/// <summary>Options for <see cref="WebhookSignatureVerifier"/>.</summary>
public sealed record WebhookSignatureVerifierOptions
{
    /// <summary>Scheme the sender uses. Default <see cref="WebhookSignatureScheme.TimestampedHmacSha256V1"/>.</summary>
    public WebhookSignatureScheme Scheme { get; init; } = WebhookSignatureScheme.TimestampedHmacSha256V1;

    /// <summary>Maximum allowed difference between the request timestamp and the receiver clock (1 s – 1 h). Default 5 minutes.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Verifies signatures on received webhook requests. Intended for receivers (and tests); senders use
/// <see cref="WebhookDeliveryService"/>. Accepts any of several keys so receivers can rotate secrets.
/// </summary>
/// <remarks>
/// A valid signature proves the request came from a holder of the key and was not modified; it does not
/// prove it is the first delivery. Receivers must still de-duplicate on <see cref="WebhookHeaders.IdempotencyKey"/>
/// because delivery is at-least-once.
/// </remarks>
public sealed class WebhookSignatureVerifier
{
    private readonly WebhookSigningKey[] _keys;
    private readonly WebhookSignatureVerifierOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a verifier.</summary>
    /// <param name="keys">Keys to accept. During rotation, include both the old and the new key.</param>
    /// <param name="options">Verifier options.</param>
    /// <param name="timeProvider">Clock used for the timestamp tolerance check.</param>
    public WebhookSignatureVerifier(IEnumerable<WebhookSigningKey> keys, WebhookSignatureVerifierOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys.ToArray();
        if (_keys.Length == 0)
        {
            throw new WebhookConfigurationException("At least one verification key is required.");
        }

        _options = options ?? new WebhookSignatureVerifierOptions();
        Validation.Require(_options.Tolerance >= TimeSpan.FromSeconds(1) && _options.Tolerance <= TimeSpan.FromHours(1), "Verifier Tolerance must be between 1 second and 1 hour.");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Verifies a request given a header lookup (case-insensitive lookup recommended) and the raw body bytes.</summary>
    /// <param name="getHeader">Returns the header value or null when absent.</param>
    /// <param name="body">The raw request body exactly as received. Do not re-serialise parsed JSON.</param>
    public WebhookVerificationStatus Verify(Func<string, string?> getHeader, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(getHeader);

        if (_options.Scheme == WebhookSignatureScheme.BodyHmacSha256)
        {
            var legacy = getHeader(WebhookHeaders.LegacySignature);
            if (legacy is null)
            {
                return WebhookVerificationStatus.MissingHeader;
            }

            var hex = WebhookSigner.ParseLegacySignature(legacy);
            if (hex is null)
            {
                return WebhookVerificationStatus.MalformedHeader;
            }

            foreach (var key in _keys)
            {
                if (FixedTimeEqualsHex(WebhookSigner.ComputeBodyHex(key.Secret, body), hex))
                {
                    return WebhookVerificationStatus.Valid;
                }
            }

            return WebhookVerificationStatus.SignatureMismatch;
        }

        var timestamp = getHeader(WebhookHeaders.Timestamp);
        var signature = getHeader(WebhookHeaders.Signature);
        var idempotencyKey = getHeader(WebhookHeaders.IdempotencyKey);
        var eventType = getHeader(WebhookHeaders.Event);
        if (timestamp is null || signature is null || idempotencyKey is null || eventType is null)
        {
            return WebhookVerificationStatus.MissingHeader;
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)
            || !Validation.IsValidIdempotencyKey(idempotencyKey)
            || !Validation.IsValidEventType(eventType))
        {
            return WebhookVerificationStatus.MalformedHeader;
        }

        var nowSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(nowSeconds - unixSeconds) > (long)_options.Tolerance.TotalSeconds)
        {
            return WebhookVerificationStatus.TimestampOutOfTolerance;
        }

        var candidates = WebhookSigner.ParseV1Signatures(signature).Take(16).ToList();
        if (candidates.Count == 0)
        {
            return WebhookVerificationStatus.MalformedHeader;
        }

        foreach (var key in _keys)
        {
            var expected = WebhookSigner.ComputeV1Hex(key.Secret, timestamp, idempotencyKey, eventType, body);
            foreach (var candidate in candidates)
            {
                if (FixedTimeEqualsHex(expected, candidate))
                {
                    return WebhookVerificationStatus.Valid;
                }
            }
        }

        return WebhookVerificationStatus.SignatureMismatch;
    }

    private static bool FixedTimeEqualsHex(string expectedLowerHex, string candidate)
    {
        // Lengths are public (always 64 hex chars for SHA-256), so an early length check leaks nothing secret.
        if (candidate.Length != expectedLowerHex.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedLowerHex),
            Encoding.ASCII.GetBytes(candidate.ToLowerInvariant()));
    }
}
