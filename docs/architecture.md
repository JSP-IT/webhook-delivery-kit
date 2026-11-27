# Architecture

## Components

```
            ┌──────────────────────────── your process ────────────────────────────┐
            │                                                                       │
 app code ──┼─► WebhookDeliveryService.EnqueueAsync ──► IDeliveryStore ◄──┐         │
            │        (validate, hash, idempotent insert)   (SQLite, …)    │         │
            │                                                             │ lease / │
            │   WebhookDeliveryService.RunAsync / ProcessDueAsync ────────┘ complete│
            │        │                                                              │
            │        ├─► destination registry (configured ids only)                 │
            │        ├─► WebhookSigner (HMAC-SHA256, all active keys)               │
            │        └─► per-destination HttpMessageInvoker                         │
            │               SocketsHttpHandler: no redirects, no proxy, TLS1.2+,    │
            │               ConnectCallback ─► DNS ─► NetworkAddressPolicy check    │
            └───────────────────────────────────────┬───────────────────────────────┘
                                                    │ HTTPS POST
                                                    ▼
                                         receiver (WebhookSignatureVerifier,
                                                   de-dup on Idempotency-Key)
```

| Type | Responsibility |
| --- | --- |
| `WebhookDeliveryService` | Public facade: enqueue, get, cancel, `ProcessDueAsync` (one batch) and `RunAsync` (loop), runtime `UpsertDestination` |
| `WebhookDestination` | Explicit endpoint configuration: URL, keys, retry policy, timeout, network opt-ins, static headers |
| `RetryPolicy` | Bounded exponential backoff with jitter, `Retry-After` handling and maximum age |
| `WebhookSigner` / `WebhookSignatureVerifier` | Signature computation, and verification for receivers |
| `IDeliveryStore` | Persistence contract. `InMemoryDeliveryStore` and `SqliteDeliveryStore` implement it |
| `NetworkAddressPolicy` | Decides which IP addresses may be connected to (SSRF guard) |
| `Redaction` | Endpoint and error-text sanitising for logs and stored errors |

The core package depends only on `Microsoft.Extensions.Logging.Abstractions`. It has no hosting or
dependency-injection requirements, so it can be embedded in any .NET 10 process: ASP.NET Core, a worker
service or a console app.

## Delivery lifecycle

```
                 EnqueueAsync
                      │
                      ▼
   ┌──────────►  Pending ────────── CancelAsync ──────────┐
   │                │ lease (due)                          │
   │ retryable      ▼                                      ▼
   │ failure    InFlight ───────── CancelAsync ───────► Cancelled
   │ (backoff)      │ │ lease expired (crash/stall)
   └────────────────┘ └──► re-leased (attempt+1)
                    │
          2xx       │       non-retryable / attempts or age exhausted
     ┌──────────────┴──────────────┐
     ▼                             ▼
 Succeeded                       Failed
```

Statuses and attempt outcomes are persisted as integers. Their numeric values never change (see [versioning](versioning.md)).

## Dispatch

`ProcessDueAsync` does the following:

1. It calls `IDeliveryStore.LeaseDueAsync(now, BatchSize, InstanceId, LeaseDuration)`. This atomically claims records that are
   `Pending` and due, or `InFlight` with an expired lease. Each claim gets a fresh **lease token** and
   increments `AttemptCount`.
2. For each record, with at most `MaxConcurrency` running at once:
   1. It resolves the destination **by id from the current configuration**. Unknown ids fail the delivery: a message
      is never redirected to another endpoint.
   2. It checks the maximum age and the attempt budget.
   3. It signs the request with the keys active *now*, so key rotation also applies to retries.
   4. It sends with a per-attempt timeout. The response body is never read or stored.
   5. It classifies the result (success, retryable, permanent, cancelled or stopping) and computes the next attempt time.
   6. It calls `CompleteAttemptAsync`, which appends the attempt to history and applies the new state **only if the lease
      token still matches** (fencing).

`RunAsync` repeats this. It waits `PollInterval` when a batch was not full, and wakes early when a message is
enqueued in the same instance. Store errors are logged and the loop continues. Several service instances can
share one store to scale out, because leases keep each delivery to one attempt at a time.

## Leases and crash recovery

A lease reserves a delivery for `LeaseDuration`, which must be at least the longest destination timeout plus 5 s.

- **Crash before the request is sent:** the lease expires, another instance (or the restarted process) leases
  the record again, and the attempt counter shows the lost attempt. `LastErrorCategory` is set to `lease_expired`.
- **Crash after the request is sent, before the outcome is recorded:** the same recovery applies, so the receiver
  **gets the request twice** with the same `Idempotency-Key`. This is the main reason delivery is at-least-once.
- **Graceful stop during an attempt:** the request is aborted and the record is released immediately
  (`dispatcher_stopping`), so the next attempt does not wait for the lease to expire. The aborted attempt still
  counts toward `MaxAttempts`, because attempts are counted when leased.
- **Slow store write after a send:** if the lease was reclaimed in the meantime, the late completion is
  rejected by the token check and logged. It cannot overwrite the newer attempt's result.

Crashed attempts count toward `MaxAttempts`. This bounds how many times a payload that kills the process can be retried.

## Cancellation

There are two kinds of cancellation:

- **Delivery cancellation** (`CancelAsync(id)`). It marks the record `Cancelled` in the store, which wins over any in-flight
  completion through the lease check, and aborts the HTTP request if the attempt is running in this instance.
- **Dispatcher cancellation** (the `CancellationToken` passed to `RunAsync` or `ProcessDueAsync`, or `DisposeAsync`). It stops
  taking work, aborts running attempts and releases them for retry. `DisposeAsync` waits up to 30 s for
  running attempts to record their outcome.

## SQLite reference store

- The tables are `wdk_schema`, `wdk_deliveries` and `wdk_attempts`, prefixed so they can live in an application database.
- It runs in WAL mode with a busy timeout. Times are stored as Unix milliseconds.
- Leasing is a single `UPDATE … WHERE id IN (SELECT … LIMIT n) RETURNING …` statement, which is atomic across
  processes that share a database file on a local disk. Network file shares are not supported.
- Completion is one transaction: it inserts the attempt and applies a conditional update.
- The schema is versioned. A database with a newer schema version than the library knows is refused.
- `PurgeAsync(completedBefore)` deletes old terminal deliveries. Schedule it yourself, because payloads otherwise accumulate.

## Non-goals for v0.1.0

The following are out of scope for this release:

- Inbound webhook handling, beyond the verifier helper.
- Per-destination ordering, rate limiting, circuit breaking and dead-letter replay tooling.
- Proxy support. A proxy would bypass the IP checks. See [security](security.md#proxies).
- Dependency-injection and hosting extensions (`AddWebhookDelivery`, `IHostedService`). The sample shows the
  manual wiring, and these are candidates for a later minor release.
- Additional stores, such as PostgreSQL or SQL Server. Implementers can follow the `IDeliveryStore` contract and copy the contract tests.
