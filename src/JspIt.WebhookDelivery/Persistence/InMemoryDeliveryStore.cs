namespace JspIt.WebhookDelivery.Persistence;

/// <summary>
/// Process-local <see cref="IDeliveryStore"/> for tests and development. Deliveries are lost when the
/// process exits, so this store does not provide durability; use a persistent store in production.
/// </summary>
public sealed class InMemoryDeliveryStore : IDeliveryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeliveryRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Destination, string Key), string> _byKey = new();
    private readonly Dictionary<string, List<DeliveryAttempt>> _attempts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<EnqueueResult> EnqueueAsync(NewDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        lock (_gate)
        {
            if (_byKey.TryGetValue((delivery.DestinationId, delivery.IdempotencyKey), out var existingId))
            {
                return Task.FromResult(new EnqueueResult(_byId[existingId], Created: false));
            }

            if (_byId.ContainsKey(delivery.Id))
            {
                throw new InvalidOperationException($"Delivery id '{delivery.Id}' already exists.");
            }

            var record = new DeliveryRecord
            {
                Id = delivery.Id,
                DestinationId = delivery.DestinationId,
                IdempotencyKey = delivery.IdempotencyKey,
                EventType = delivery.EventType,
                ContentType = delivery.ContentType,
                Payload = delivery.Payload.ToArray(),
                PayloadSha256 = delivery.PayloadSha256,
                Status = DeliveryStatus.Pending,
                AttemptCount = 0,
                CreatedAt = delivery.CreatedAt,
                UpdatedAt = delivery.CreatedAt,
                NextAttemptAt = delivery.CreatedAt,
                ExpiresAt = delivery.ExpiresAt,
            };
            _byId[record.Id] = record;
            _byKey[(record.DestinationId, record.IdempotencyKey)] = record.Id;
            return Task.FromResult(new EnqueueResult(record, Created: true));
        }
    }

    /// <inheritdoc />
    public Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_byId.GetValueOrDefault(deliveryId));
        }
    }

    /// <inheritdoc />
    public Task<DeliveryRecord?> FindByIdempotencyKeyAsync(string destinationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_byKey.TryGetValue((destinationId, idempotencyKey), out var id) ? _byId[id] : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeliveryRecord>> LeaseDueAsync(LeaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var due = _byId.Values
                .Where(r => (r.Status == DeliveryStatus.Pending && r.NextAttemptAt <= request.Now)
                         || (r.Status == DeliveryStatus.InFlight && r.LeaseExpiresAt <= request.Now))
                .OrderBy(r => r.NextAttemptAt)
                .ThenBy(r => r.CreatedAt)
                .Take(request.MaxCount)
                .ToList();

            var leased = new List<DeliveryRecord>(due.Count);
            foreach (var record in due)
            {
                var reclaimed = record.Status == DeliveryStatus.InFlight;
                var updated = record with
                {
                    Status = DeliveryStatus.InFlight,
                    AttemptCount = record.AttemptCount + 1,
                    LeaseOwner = request.LeaseOwner,
                    LeaseToken = Guid.NewGuid().ToString("N"),
                    LeaseExpiresAt = request.Now + request.LeaseDuration,
                    UpdatedAt = request.Now,
                    LastErrorCategory = reclaimed ? DeliveryErrorCategories.LeaseExpired : record.LastErrorCategory,
                    LastError = reclaimed ? "Previous attempt's lease expired before it completed (process crash or stall)." : record.LastError,
                };
                _byId[record.Id] = updated;
                leased.Add(updated);
            }

            return Task.FromResult<IReadOnlyList<DeliveryRecord>>(leased);
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAttemptAsync(AttemptCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            if (!_byId.TryGetValue(completion.DeliveryId, out var record))
            {
                return Task.FromResult(false);
            }

            if (!_attempts.TryGetValue(record.Id, out var history))
            {
                _attempts[record.Id] = history = [];
            }

            history.Add(new DeliveryAttempt
            {
                DeliveryId = record.Id,
                AttemptNumber = completion.AttemptNumber,
                LeaseOwner = completion.LeaseOwner,
                StartedAt = completion.StartedAt,
                CompletedAt = completion.CompletedAt,
                Outcome = completion.Outcome,
                StatusCode = completion.StatusCode,
                ErrorCategory = completion.ErrorCategory,
                ErrorMessage = completion.ErrorMessage,
            });

            if (record.Status != DeliveryStatus.InFlight || record.LeaseToken != completion.LeaseToken)
            {
                return Task.FromResult(false);
            }

            _byId[record.Id] = StoreRules.ApplyCompletion(record, completion);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<CancelResult> CancelAsync(string deliveryId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(deliveryId, out var record))
            {
                return Task.FromResult(CancelResult.NotFound);
            }

            if (record.IsTerminal)
            {
                return Task.FromResult(CancelResult.AlreadyTerminal);
            }

            _byId[deliveryId] = record with
            {
                Status = DeliveryStatus.Cancelled,
                CompletedAt = now,
                UpdatedAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                LastErrorCategory = DeliveryErrorCategories.Cancelled,
                LastError = "Cancelled by the application.",
            };
            return Task.FromResult(CancelResult.Cancelled);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DeliveryAttempt> result = _attempts.TryGetValue(deliveryId, out var history)
                ? history.OrderBy(a => a.AttemptNumber).ThenBy(a => a.CompletedAt).ToList()
                : [];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var victims = _byId.Values.Where(r => r.IsTerminal && r.CompletedAt < completedBefore).ToList();
            foreach (var record in victims)
            {
                _byId.Remove(record.Id);
                _byKey.Remove((record.DestinationId, record.IdempotencyKey));
                _attempts.Remove(record.Id);
            }

            return Task.FromResult(victims.Count);
        }
    }
}

/// <summary>Shared state-transition rules so that store implementations behave identically.</summary>
public static class StoreRules
{
    /// <summary>Applies an attempt completion to a leased record and returns the new record.</summary>
    public static DeliveryRecord ApplyCompletion(DeliveryRecord record, AttemptCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(completion);

        var (status, completedAt) = completion.Outcome switch
        {
            AttemptOutcome.Succeeded => (DeliveryStatus.Succeeded, (DateTimeOffset?)completion.CompletedAt),
            AttemptOutcome.RetryScheduled => (DeliveryStatus.Pending, null),
            AttemptOutcome.Failed => (DeliveryStatus.Failed, completion.CompletedAt),
            AttemptOutcome.Cancelled => (DeliveryStatus.Cancelled, completion.CompletedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(completion), "Unknown outcome."),
        };

        if (completion.Outcome == AttemptOutcome.RetryScheduled && completion.NextAttemptAt is null)
        {
            throw new ArgumentException("RetryScheduled requires NextAttemptAt.", nameof(completion));
        }

        return record with
        {
            Status = status,
            CompletedAt = completedAt,
            UpdatedAt = completion.CompletedAt,
            NextAttemptAt = completion.NextAttemptAt ?? record.NextAttemptAt,
            LastAttemptAt = completion.StartedAt,
            LastStatusCode = completion.StatusCode,
            LastErrorCategory = completion.Outcome == AttemptOutcome.Succeeded ? null : completion.ErrorCategory,
            LastError = completion.Outcome == AttemptOutcome.Succeeded ? null : completion.ErrorMessage,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
        };
    }
}
