namespace JspIt.WebhookDelivery;

/// <summary>Request signature formats.</summary>
public enum WebhookSignatureScheme
{
    /// <summary>
    /// Default and recommended. Signs a timestamp, the idempotency key, the event type and the raw body:
    /// <code>
    /// signed = "v1\n" + Webhook-Timestamp + "\n" + Idempotency-Key + "\n" + Webhook-Event + "\n" + body
    /// Webhook-Signature: v1=&lt;lowercase hex HMAC-SHA256&gt;[, v1=&lt;hex&gt; ...]
    /// </code>
    /// One <c>v1=</c> entry is sent per active signing key. Receivers accept the request when any entry
    /// matches a key they hold and the timestamp is within their tolerance, which defeats replay of
    /// captured requests outside that window.
    /// </summary>
    TimestampedHmacSha256V1 = 0,

    /// <summary>
    /// Compatibility mode for receivers that already verify a body-only signature:
    /// <code>X-Webhook-Signature: sha256=&lt;lowercase hex HMAC-SHA256(body)&gt;</code>
    /// Only the first active key signs. This format carries no timestamp, so captured requests can be
    /// replayed; receivers must de-duplicate on <c>Idempotency-Key</c>. Prefer the default scheme.
    /// </summary>
    BodyHmacSha256 = 1,
}
