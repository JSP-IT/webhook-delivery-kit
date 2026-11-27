namespace JspIt.WebhookDelivery;

/// <summary>Header names used on outbound webhook requests.</summary>
public static class WebhookHeaders
{
    /// <summary>Prefix reserved for protocol headers; destinations cannot configure headers starting with it.</summary>
    public const string ReservedPrefix = "Webhook-";

    /// <summary>Stable across all attempts of one delivery. Receivers should de-duplicate on it.</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The event type supplied at enqueue time.</summary>
    public const string Event = "Webhook-Event";

    /// <summary>1-based attempt number. Informational only; not signed.</summary>
    public const string Attempt = "Webhook-Attempt";

    /// <summary>Unix time in seconds at which the attempt was signed (fresh per attempt).</summary>
    public const string Timestamp = "Webhook-Timestamp";

    /// <summary>Signature header for <see cref="WebhookSignatureScheme.TimestampedHmacSha256V1"/>.</summary>
    public const string Signature = "Webhook-Signature";

    /// <summary>Signature header for <see cref="WebhookSignatureScheme.BodyHmacSha256"/>.</summary>
    public const string LegacySignature = "X-Webhook-Signature";
}
