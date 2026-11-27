using System.Text;

namespace JspIt.WebhookDelivery;

/// <summary>A message to deliver to a configured destination.</summary>
public sealed record WebhookMessage
{
    /// <summary>Creates a message.</summary>
    /// <param name="destinationId">Id of a configured <see cref="WebhookDestination"/>.</param>
    /// <param name="eventType">Event type, e.g. <c>invoice.paid</c>. Letters, digits and <c>. _ : / -</c>, starting with a letter or digit, max 128.</param>
    /// <param name="payload">Request body, sent verbatim and signed byte-for-byte.</param>
    /// <param name="idempotencyKey">
    /// Caller-chosen key, unique per destination, e.g. <c>evt_123</c>. Derive it from your business event id so that
    /// re-enqueueing the same event (for example after your own crash) does not create a second delivery.
    /// Visible ASCII, max 200 characters.
    /// </param>
    public WebhookMessage(string destinationId, string eventType, ReadOnlyMemory<byte> payload, string idempotencyKey)
    {
        DestinationId = destinationId;
        EventType = eventType;
        Payload = payload;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>Destination id.</summary>
    public string DestinationId { get; init; }

    /// <summary>Event type.</summary>
    public string EventType { get; init; }

    /// <summary>Request body.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Idempotency key.</summary>
    public string IdempotencyKey { get; init; }

    /// <summary>Request content type. Default <c>application/json</c>.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>Creates a message whose body is the UTF-8 encoding of <paramref name="json"/>.</summary>
    public static WebhookMessage FromJson(string destinationId, string eventType, string json, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new WebhookMessage(destinationId, eventType, Encoding.UTF8.GetBytes(json), idempotencyKey);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"WebhookMessage(destination={DestinationId}, event={EventType}, key={IdempotencyKey}, payload=[{Payload.Length} bytes])";
}
