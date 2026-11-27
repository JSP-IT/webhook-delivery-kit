# Delivery semantics

## The guarantee: at-least-once

Once `EnqueueAsync` returns, the message is in the store. From then on the library keeps attempting it
until one of these happens:

- a `2xx` response is received (**Succeeded**),
- a non-retryable outcome occurs, the attempts run out, or the delivery gets too old (**Failed**),
- the application cancels it (**Cancelled**).

**The library never claims exactly-once delivery.** Exactly-once across a network is not achievable without
the receiver's cooperation. What this library offers is *at-least-once delivery plus a stable idempotency
key*, which lets a receiver achieve *effectively-once processing* by de-duplicating.

Durability depends on the store. `SqliteDeliveryStore` survives process restarts. `InMemoryDeliveryStore`
does not, and is meant for tests and development only.

## When duplicates happen

| Scenario | What the receiver sees |
| --- | --- |
| The receiver processes the request, but its response is lost (network reset or proxy failure) | The same request again on the next attempt |
| The receiver responds after the destination `Timeout` | The attempt counts as timed out and is retried, even though the receiver may already have processed it |
| The sender process crashes after sending but before recording the outcome | The lease expires and another attempt is made (see [architecture](architecture.md#leases-and-crash-recovery)) |
| The dispatcher stops (shutdown or cancellation) while a request is in flight | The delivery is released and retried. The receiver may already have it |
| `CancelAsync` is called while a request is in flight | The request is aborted, but the receiver may already have read it |

Every attempt for a delivery carries the **same `Idempotency-Key`**. `Webhook-Attempt` increments, and
`Webhook-Timestamp` and `Webhook-Signature` are fresh on each attempt.

## What receivers must do

1. **Verify the signature** against the raw body bytes (see [security](security.md#verifying-requests)).
2. **De-duplicate on `Idempotency-Key`.** Store processed keys at least as long as the sender's
   `RetryPolicy.MaxDeliveryAge` (24 h by default), and preferably longer. Make the check-and-record
   atomic with the side effect where possible, for example a unique constraint in the same transaction.
3. **Respond quickly with `2xx`**, then do heavy work asynchronously. Slow responses risk timeouts, which
   lead to duplicates.
4. **Use status codes deliberately:**
   - `2xx`: accepted. The delivery is finished.
   - `408`, `425`, `429`, `5xx`: retried. `Retry-After` on `429` or `503` is honoured, up to `MaxDelay`.
   - Other `4xx`: permanent failure, not retried. Use this for a payload that will never be acceptable.
   - `3xx`: permanent failure. **Redirects are never followed.**

## Sender-side idempotency

`EnqueueAsync` is idempotent per (destination, idempotency key). If your own process crashes after
committing a business transaction but before (or during) enqueueing, re-enqueueing with the same key is safe:
it returns the existing delivery (`Created = false`) and does not send twice. Reusing a key with a
**different** payload, event type or content type throws `IdempotencyConflictException`.

Derive the key from your business event id (for example `evt_<event_id>`), not from a random value generated at
enqueue time. A random key defeats sender-side de-duplication.

For full atomicity between your business data and the enqueue, use a store in the same database as your data
and enqueue inside your transaction. That is the transactional outbox pattern. The SQLite reference store opens its
own connections, so it does not join an ambient transaction in v0.1.0.

## Ordering

**Ordering is not guaranteed.** Deliveries are leased oldest-due first, but concurrency, retries and backoff
mean a later message can arrive before an earlier one. Receivers that care about order should use a sequence
number or timestamp in the payload and handle out-of-order arrival. Per-destination ordered delivery is a
possible future feature and is not part of v0.1.0.

## Retry timing

The delay after attempt *n* is `InitialDelay × BackoffMultiplier^(n−1)`, capped at `MaxDelay`. When jitter
is on, the delay is randomised within `[delay/2, delay]`. The defaults are 8 attempts, 10 s initial delay,
×2 multiplier, 1 h cap and 24 h maximum age. That gives roughly 10 s, 20 s, 40 s, 80 s, 160 s, 320 s and 640 s
between attempts, before jitter.

A delivery fails without another attempt once any of these limits is reached:

- `MaxAttempts`. Attempts that crashed or stalled count too, which limits the damage from a payload that
  keeps crashing the sender.
- `MaxDeliveryAge`, measured from enqueue time.
