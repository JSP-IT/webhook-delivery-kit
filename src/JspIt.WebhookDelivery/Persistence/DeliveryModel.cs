namespace JspIt.WebhookDelivery.Persistence;

/// <summary>Lifecycle state of a delivery. Values are persisted; never renumber them.</summary>
public enum DeliveryStatus
{
    /// <summary>Waiting for its next attempt (<see cref="DeliveryRecord.NextAttemptAt"/>).</summary>
    Pending = 0,

    /// <summary>Leased by a dispatcher and being attempted. Reclaimed if the lease expires (e.g. after a crash).</summary>
    InFlight = 1,

    /// <summary>Terminal: the receiver answered with a 2xx status.</summary>
    Succeeded = 2,

    /// <summary>Terminal: a non-retryable outcome, or retries/age exhausted.</summary>
    Failed = 3,

    /// <summary>Terminal: cancelled by the application.</summary>
    Cancelled = 4,
}

/// <summary>Outcome of one attempt. Values are persisted; never renumber them.</summary>
public enum AttemptOutcome
{
    /// <summary>2xx response; the delivery is complete.</summary>
    Succeeded = 0,

    /// <summary>Retryable failure; another attempt is scheduled.</summary>
    RetryScheduled = 1,

    /// <summary>Non-retryable failure or retries exhausted; the delivery is failed.</summary>
    Failed = 2,

    /// <summary>The attempt was aborted because the delivery was cancelled.</summary>
    Cancelled = 3,
}

/// <summary>Stable, machine-readable error categories stored with failed attempts.</summary>
public static class DeliveryErrorCategories
{
    /// <summary>Non-2xx HTTP status.</summary>
    public const string HttpStatus = "http_status";

    /// <summary>The receiver answered with a redirect, which is never followed.</summary>
    public const string RedirectNotFollowed = "redirect_not_followed";

    /// <summary>The attempt exceeded the destination timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>DNS, connection, TLS or protocol error.</summary>
    public const string Network = "network";

    /// <summary>The resolved address is not permitted by the destination's network policy (SSRF guard).</summary>
    public const string DestinationBlocked = "destination_blocked";

    /// <summary>The destination is no longer configured.</summary>
    public const string UnknownDestination = "unknown_destination";

    /// <summary>No signing key is active.</summary>
    public const string NoActiveSigningKey = "no_active_signing_key";

    /// <summary>The previous attempt's lease expired before it completed (process crash or stall).</summary>
    public const string LeaseExpired = "lease_expired";

    /// <summary>All attempts were used.</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>The delivery exceeded its maximum age.</summary>
    public const string Expired = "expired";

    /// <summary>The dispatcher was stopping; the delivery was released for another attempt.</summary>
    public const string DispatcherStopping = "dispatcher_stopping";

    /// <summary>The delivery was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>An unexpected error occurred while preparing or sending the request; the attempt is retried.</summary>
    public const string Unexpected = "unexpected_error";
}

/// <summary>A persisted webhook delivery. Instances are snapshots; re-read to observe changes.</summary>
public sealed record DeliveryRecord
{
    /// <summary>Unique delivery id.</summary>
    public required string Id { get; init; }

    /// <summary>Configured destination id.</summary>
    public required string DestinationId { get; init; }

    /// <summary>Caller-supplied idempotency key, unique per destination and sent on every attempt.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Request content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Request body. Persisted so deliveries survive restarts; treat as potentially sensitive.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Payload"/>, used to detect idempotency-key reuse with different content.</summary>
    public required string PayloadSha256 { get; init; }

    /// <summary>Current status.</summary>
    public required DeliveryStatus Status { get; init; }

    /// <summary>Number of attempts started (incremented when leased, so crashed attempts count).</summary>
    public required int AttemptCount { get; init; }

    /// <summary>Creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last modification time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Earliest time of the next attempt (meaningful while <see cref="DeliveryStatus.Pending"/>).</summary>
    public required DateTimeOffset NextAttemptAt { get; init; }

    /// <summary>After this instant the delivery is failed instead of attempted.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Start time of the latest completed attempt.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>When the delivery reached a terminal status.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>HTTP status of the latest completed attempt, if a response was received.</summary>
    public int? LastStatusCode { get; init; }

    /// <summary>Error category of the latest failure (see <see cref="DeliveryErrorCategories"/>).</summary>
    public string? LastErrorCategory { get; init; }

    /// <summary>Redacted, truncated error text of the latest failure.</summary>
    public string? LastError { get; init; }

    /// <summary>Owner of the current lease (dispatcher instance id).</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>Fencing token of the current lease. Completions must present it.</summary>
    public string? LeaseToken { get; init; }

    /// <summary>When the current lease expires.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>Whether <see cref="Status"/> is terminal.</summary>
    public bool IsTerminal => Status is DeliveryStatus.Succeeded or DeliveryStatus.Failed or DeliveryStatus.Cancelled;

    /// <inheritdoc />
    public override string ToString() =>
        $"DeliveryRecord({Id}, destination={DestinationId}, status={Status}, attempts={AttemptCount}, payload=[{Payload.Length} bytes])";
}

/// <summary>A historical attempt.</summary>
public sealed record DeliveryAttempt
{
    /// <summary>Delivery id.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Lease owner that made the attempt.</summary>
    public required string LeaseOwner { get; init; }

    /// <summary>Attempt start.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Attempt end.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Outcome.</summary>
    public required AttemptOutcome Outcome { get; init; }

    /// <summary>HTTP status, if any response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Error category.</summary>
    public string? ErrorCategory { get; init; }

    /// <summary>Redacted error text.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>A new delivery to insert.</summary>
public sealed record NewDelivery
{
    /// <summary>Unique delivery id.</summary>
    public required string Id { get; init; }

    /// <summary>Destination id.</summary>
    public required string DestinationId { get; init; }

    /// <summary>Idempotency key, unique per destination.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Body.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Lowercase hex SHA-256 of the payload.</summary>
    public required string PayloadSha256 { get; init; }

    /// <summary>Creation time; also the first attempt time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Expiry time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Result of <see cref="IDeliveryStore.EnqueueAsync"/>.</summary>
/// <param name="Record">The stored record (new or pre-existing).</param>
/// <param name="Created">False when a delivery with the same destination and idempotency key already existed.</param>
public sealed record EnqueueResult(DeliveryRecord Record, bool Created);

/// <summary>Parameters for <see cref="IDeliveryStore.LeaseDueAsync"/>.</summary>
/// <param name="Now">Current time.</param>
/// <param name="MaxCount">Maximum records to lease.</param>
/// <param name="LeaseOwner">Dispatcher instance id.</param>
/// <param name="LeaseDuration">Lease length.</param>
public sealed record LeaseRequest(DateTimeOffset Now, int MaxCount, string LeaseOwner, TimeSpan LeaseDuration);

/// <summary>Result of an attempt, written by <see cref="IDeliveryStore.CompleteAttemptAsync"/>.</summary>
public sealed record AttemptCompletion
{
    /// <summary>Delivery id.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>Lease fencing token returned by the lease.</summary>
    public required string LeaseToken { get; init; }

    /// <summary>Lease owner.</summary>
    public required string LeaseOwner { get; init; }

    /// <summary>Attempt number (the record's <see cref="DeliveryRecord.AttemptCount"/> at lease time).</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Attempt start.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Attempt end.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Outcome. <see cref="AttemptOutcome.RetryScheduled"/> requires <see cref="NextAttemptAt"/>.</summary>
    public required AttemptOutcome Outcome { get; init; }

    /// <summary>Next attempt time when retrying.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>HTTP status, if any.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Error category.</summary>
    public string? ErrorCategory { get; init; }

    /// <summary>Redacted error text.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Result of <see cref="IDeliveryStore.CancelAsync"/>.</summary>
public enum CancelResult
{
    /// <summary>The delivery was pending or in flight and is now cancelled.</summary>
    Cancelled,

    /// <summary>The delivery had already reached a terminal status; nothing changed.</summary>
    AlreadyTerminal,

    /// <summary>No delivery with that id exists.</summary>
    NotFound,
}
