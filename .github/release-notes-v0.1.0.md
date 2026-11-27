# Webhook Delivery Kit v0.1.0

First pre-release of an embeddable .NET 10 library for reliable outbound webhook delivery.

> **Delivery is at-least-once, not exactly-once.** Receivers can get the same message more than once and
> must de-duplicate on the `Idempotency-Key` header, which is identical on every attempt.

## Packages

- `JspIt.WebhookDelivery` 0.1.0: delivery engine, signing and verification, retry policy, `IDeliveryStore` and an in-memory store.
- `JspIt.WebhookDelivery.Sqlite` 0.1.0: SQLite reference store.

## Highlights

- **Durable delivery records** with attempt history. Leases with fencing tokens let several instances share one
  store, and expired leases are reclaimed after a crash.
- **Configurable bounded retries.** Exponential backoff with jitter, `Retry-After` on 429 and 503, and limits on
  attempts and age. Crashed attempts count toward the attempt limit.
- **Idempotency keys.** Enqueue is idempotent per destination and key, and the key is sent on every attempt.
- **HMAC-SHA256 signatures** over a timestamp, the key, the event type and the body. Multiple active keys allow
  rotation without downtime, and a body-only compatibility mode supports existing receivers.
- **Cancellation** of pending and in-flight deliveries, plus graceful dispatcher shutdown.
- **Secure defaults:**
  - Only explicitly configured destinations can be targeted, so the library is not an open URL relay.
  - HTTPS with TLS 1.2+ is required.
  - SSRF checks run on resolved IPs at connect time.
  - Redirects are never followed, and no proxy is used.
  - Timeouts and limits have hard bounds.
  - Logs and stored errors are redacted.

## Compatibility

- Requires .NET 10.
- Pre-1.0: minor versions may contain breaking API changes. The `v1` wire format and SQLite schema version 1
  are frozen. See `docs/versioning.md`.

## Known limitations

- Ordering is not guaranteed.
- There is no proxy support, no mTLS, and no DI or hosting extension package yet.
- The SQLite store does not join an ambient application transaction, so it does not give a full transactional outbox.
- Payloads are stored unencrypted in the store. Protect the database and schedule `PurgeAsync`.

## Not published

These notes and the package metadata were prepared for review. Nothing has been pushed to NuGet.
