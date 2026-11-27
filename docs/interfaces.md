# Interfaces

All public types are in `JspIt.WebhookDelivery` and `JspIt.WebhookDelivery.Persistence` (core package), and
`JspIt.WebhookDelivery.Sqlite` (SQLite package). XML documentation ships with the packages.

## `WebhookDeliveryService`

```csharp
public sealed class WebhookDeliveryService : IAsyncDisposable
{
    public WebhookDeliveryService(IDeliveryStore store, WebhookDeliveryOptions options,
                                  ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null);

    Task<EnqueueResult> EnqueueAsync(WebhookMessage message, CancellationToken ct = default);
    Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken ct = default);
    Task<DeliveryRecord?> FindAsync(string destinationId, string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken ct = default);
    Task<CancelResult> CancelAsync(string deliveryId, CancellationToken ct = default);

    Task<int> ProcessDueAsync(CancellationToken ct = default);  // one batch, returns count processed
    Task RunAsync(CancellationToken ct = default);              // loop until cancelled/disposed; returns normally

    void UpsertDestination(WebhookDestination destination);     // runtime add/replace (e.g. key rotation)
    IReadOnlyCollection<string> DestinationIds { get; }
}
```

| Exception | When |
| --- | --- |
| `WebhookConfigurationException` | The configuration is invalid (constructor or `UpsertDestination`) |
| `UnknownDestinationException` | `EnqueueAsync` names a destination id that isn't configured |
| `IdempotencyConflictException` | The same (destination, key) is enqueued with different content |
| `ArgumentException` | The message is invalid: event type, key, content type, or payload over `MaxPayloadBytes` |

The `TimeProvider` drives scheduling and signing timestamps, which makes deterministic tests with
`FakeTimeProvider` possible. Network timeouts always use real time.

### Hosting in ASP.NET Core or a worker

```csharp
public sealed class WebhookDispatcher(WebhookDeliveryService service) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => service.RunAsync(stoppingToken);
}

// Program.cs
builder.Services.AddSingleton<IDeliveryStore>(_ => SqliteDeliveryStore.ForFile("webhooks.db"));
builder.Services.AddSingleton(sp => new WebhookDeliveryService(
    sp.GetRequiredService<IDeliveryStore>(), BuildOptions(builder.Configuration), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<WebhookDispatcher>();
```

## Configuration types

- `WebhookDeliveryOptions` holds `Destinations`, `BatchSize`, `MaxConcurrency`, `PollInterval`, `LeaseDuration` and `InstanceId`.
- `WebhookDestination` holds `Id`, `Url`, `SigningKeys`, `SignatureScheme`, `RetryPolicy`, `Timeout`, `MaxPayloadBytes`, `Headers`,
  `AllowInsecureHttp`, `AllowLoopback` and `AllowedPrivateNetworks`.
- `RetryPolicy` holds `MaxAttempts`, `InitialDelay`, `BackoffMultiplier`, `MaxDelay`, `UseJitter`, `RespectRetryAfter` and
  `MaxDeliveryAge`. It also provides `GetDelay(...)`, `Default` and `NoRetry`.
- `WebhookSigningKey` is created with `new(keyId, bytes, notBefore?, notAfter?)`, `FromBase64`, `FromUtf8` or `Generate`. It also provides `ExportBase64Secret()` and `IsActiveAt(...)`.
- `WebhookMessage` is created with `new(destinationId, eventType, payload, idempotencyKey)` or `FromJson(...)`, and has a `ContentType` property (default `application/json`).

See [security.md](security.md#bounded-resource-use) for the bounds of every setting.

## Receivers

- `WebhookSignatureVerifier(keys, options?, timeProvider?)` provides `Verify(Func<string, string?> getHeader, ReadOnlySpan<byte> body)`, which returns a `WebhookVerificationStatus`.
- `WebhookHeaders` holds the header name constants.
- `WebhookSigner.Sign(...)` is the low-level signing function, used by the dispatcher and useful for testing receivers.

## Persistence: `IDeliveryStore`

```csharp
public interface IDeliveryStore
{
    Task<EnqueueResult> EnqueueAsync(NewDelivery delivery, CancellationToken ct = default);
    Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken ct = default);
    Task<DeliveryRecord?> FindByIdempotencyKeyAsync(string destinationId, string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryRecord>> LeaseDueAsync(LeaseRequest request, CancellationToken ct = default);
    Task<bool> CompleteAttemptAsync(AttemptCompletion completion, CancellationToken ct = default);
    Task<CancelResult> CancelAsync(string deliveryId, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken ct = default);
    Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken ct = default);
}
```

### Contract for implementers

1. **Idempotent enqueue.** (`DestinationId`, `IdempotencyKey`) is unique. A second insert returns the existing
   record with `Created = false` and must not modify it.
2. **Atomic, exclusive leasing.** Claim up to `MaxCount` records where
   `(Status = Pending AND NextAttemptAt <= Now) OR (Status = InFlight AND LeaseExpiresAt <= Now)`, ordered by
   `NextAttemptAt`. In the same atomic step:
   - set `Status = InFlight`,
   - increment `AttemptCount`,
   - set `LeaseOwner`, a **new unique** `LeaseToken`, and `LeaseExpiresAt = Now + LeaseDuration`,
   - when reclaiming an expired `InFlight` record, set `LastErrorCategory = "lease_expired"`.

   Two concurrent callers must never receive the same record. Return the updated records.
3. **Fenced completion.** Always append the attempt to history. Apply the outcome only if the record is
   `InFlight` and `LeaseToken` matches, and return whether it was applied. On apply, clear the lease. For
   `Succeeded`, `Failed` and `Cancelled`, set `CompletedAt`. For `RetryScheduled`, set `Status = Pending` and
   `NextAttemptAt`. On success, clear the error fields. `StoreRules.ApplyCompletion` implements these transitions
   for record-based stores.
4. **Cancel.** `Pending` or `InFlight` becomes `Cancelled`, with `CompletedAt` set and the lease cleared. Terminal
   records are unchanged and return `AlreadyTerminal`. A missing record returns `NotFound`.
5. **Never store signing secrets.** The library never passes them to the store.
6. **Time precision.** Millisecond precision is enough, and the SQLite store uses Unix milliseconds.

The test project's `StoreContractTests` class encodes this contract and runs against both bundled stores.
Copy it to verify a custom store.
