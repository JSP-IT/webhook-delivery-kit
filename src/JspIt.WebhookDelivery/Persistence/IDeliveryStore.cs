namespace JspIt.WebhookDelivery.Persistence;

/// <summary>
/// Pluggable persistence for webhook deliveries. Implementations must be safe for concurrent use by
/// multiple dispatcher instances (threads and, for shared databases, processes).
/// </summary>
/// <remarks>
/// <para>Required semantics (exercised by the shared contract tests in the test project):</para>
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> is idempotent on (<c>DestinationId</c>, <c>IdempotencyKey</c>): a second insert returns the existing record with <c>Created = false</c>.</item>
/// <item><see cref="LeaseDueAsync"/> atomically claims records that are <see cref="DeliveryStatus.Pending"/> and due, or
/// <see cref="DeliveryStatus.InFlight"/> with an expired lease. A record is never leased to two callers at once. Leasing sets
/// status <see cref="DeliveryStatus.InFlight"/>, increments <see cref="DeliveryRecord.AttemptCount"/>, and issues a fresh
/// <see cref="DeliveryRecord.LeaseToken"/>. Reclaiming an expired lease sets <see cref="DeliveryRecord.LastErrorCategory"/>
/// to <see cref="DeliveryErrorCategories.LeaseExpired"/>.</item>
/// <item><see cref="CompleteAttemptAsync"/> always appends the attempt to history, but only updates the delivery when it is
/// still <see cref="DeliveryStatus.InFlight"/> under the same lease token (fencing). It returns whether the update applied.</item>
/// <item><see cref="CancelAsync"/> moves a pending or in-flight record to <see cref="DeliveryStatus.Cancelled"/>; terminal records are unchanged.</item>
/// </list>
/// <para>Stores persist payloads but never signing secrets.</para>
/// </remarks>
public interface IDeliveryStore
{
    /// <summary>Inserts a delivery, or returns the existing one for the same destination and idempotency key.</summary>
    Task<EnqueueResult> EnqueueAsync(NewDelivery delivery, CancellationToken cancellationToken = default);

    /// <summary>Gets a delivery by id.</summary>
    Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken cancellationToken = default);

    /// <summary>Gets a delivery by destination and idempotency key.</summary>
    Task<DeliveryRecord?> FindByIdempotencyKeyAsync(string destinationId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Atomically leases due deliveries, oldest <see cref="DeliveryRecord.NextAttemptAt"/> first.</summary>
    Task<IReadOnlyList<DeliveryRecord>> LeaseDueAsync(LeaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records an attempt and, if the lease is still held, applies its outcome.</summary>
    Task<bool> CompleteAttemptAsync(AttemptCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending or in-flight delivery.</summary>
    Task<CancelResult> CancelAsync(string deliveryId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Returns the attempt history of a delivery, ordered by attempt number.</summary>
    Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken cancellationToken = default);

    /// <summary>Deletes terminal deliveries (and their attempts) completed before <paramref name="completedBefore"/>. Returns the number deleted.</summary>
    Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default);
}
