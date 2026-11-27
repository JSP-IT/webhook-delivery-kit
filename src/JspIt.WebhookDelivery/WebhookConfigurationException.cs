namespace JspIt.WebhookDelivery;

/// <summary>
/// Thrown when webhook delivery configuration (a destination, retry policy, signing key or
/// service option) is invalid. Configuration is validated eagerly when the
/// <see cref="WebhookDeliveryService"/> is constructed so misconfiguration fails fast at startup.
/// </summary>
public sealed class WebhookConfigurationException : Exception
{
    /// <summary>Creates a new configuration exception.</summary>
    public WebhookConfigurationException(string message) : base(message) { }

    /// <summary>Creates a new configuration exception with an inner exception.</summary>
    public WebhookConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown by <see cref="WebhookDeliveryService.EnqueueAsync"/> when a message names a destination
/// id that is not configured. Messages can only target destinations registered up front; the
/// library never accepts a caller-supplied URL at enqueue time.
/// </summary>
public sealed class UnknownDestinationException : Exception
{
    /// <summary>Creates a new exception for the given destination id.</summary>
    public UnknownDestinationException(string destinationId)
        : base($"Webhook destination '{destinationId}' is not configured.")
    {
        DestinationId = destinationId;
    }

    /// <summary>The destination id that was not found.</summary>
    public string DestinationId { get; }
}

/// <summary>
/// Thrown when a message is enqueued with an idempotency key that already exists for the same
/// destination but with a different event type or payload. Reusing a key for different content is
/// almost always a caller bug, so the library refuses rather than silently dropping the new content.
/// </summary>
public sealed class IdempotencyConflictException : Exception
{
    /// <summary>Creates a new conflict exception.</summary>
    public IdempotencyConflictException(string destinationId, string idempotencyKey, string existingDeliveryId)
        : base($"Idempotency key '{idempotencyKey}' is already used for destination '{destinationId}' by delivery '{existingDeliveryId}' with a different event type or payload.")
    {
        DestinationId = destinationId;
        IdempotencyKey = idempotencyKey;
        ExistingDeliveryId = existingDeliveryId;
    }

    /// <summary>The destination id.</summary>
    public string DestinationId { get; }

    /// <summary>The conflicting idempotency key.</summary>
    public string IdempotencyKey { get; }

    /// <summary>The id of the delivery that already owns the key.</summary>
    public string ExistingDeliveryId { get; }
}
