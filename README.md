# Webhook Delivery Kit

An embeddable .NET library for reliable **outbound** webhook delivery: persist a message, and the library
delivers it to a pre-configured endpoint with HMAC signing, bounded retries with backoff, idempotency keys,
cancellation, and SSRF-safe networking.

> **Status: v0.1.0 (pre-release).** The public API may change in minor versions until 1.0. See [docs/versioning.md](docs/versioning.md).

| Package | Purpose |
| --- | --- |
| `JspIt.WebhookDelivery` | Delivery engine, signing/verification, retry policy, `IDeliveryStore` interface, in-memory store |
| `JspIt.WebhookDelivery.Sqlite` | SQLite reference implementation of `IDeliveryStore` |

Targets **.NET 10** (LTS).

## Delivery guarantee: at-least-once

**This library provides at-least-once delivery. It does not and cannot provide exactly-once delivery.**

A receiver can get the same message more than once, for example when its `2xx` response is lost, when it
answers after the attempt timed out, or when the sending process crashes after sending but before recording
the outcome. Every attempt for a message carries the same `Idempotency-Key` header, so **receivers must
de-duplicate on it**. A message can also fail for good: retries run out, it gets too old, the receiver
returns a non-retryable response, or someone cancels it. Read [docs/delivery-semantics.md](docs/delivery-semantics.md).

## Quick start

```csharp
using JspIt.WebhookDelivery;
using JspIt.WebhookDelivery.Sqlite;

var options = new WebhookDeliveryOptions();
options.Destinations.Add(new WebhookDestination
{
    Id = "acme-orders",                                    // messages target this id, never a URL
    Url = new Uri("https://hooks.acme.example/orders"),   // https required by default
    SigningKeys = [WebhookSigningKey.FromBase64("2026-11", secretFromVault)],
    RetryPolicy = new RetryPolicy { MaxAttempts = 8, InitialDelay = TimeSpan.FromSeconds(10) },
    Timeout = TimeSpan.FromSeconds(10),
});

using var store = SqliteDeliveryStore.ForFile("webhooks.db");
await using var service = new WebhookDeliveryService(store, options, loggerFactory);

// Run the dispatcher, e.g. from a BackgroundService.ExecuteAsync:
_ = service.RunAsync(stoppingToken);

// Enqueue. The idempotency key should come from your business event id.
var result = await service.EnqueueAsync(
    WebhookMessage.FromJson("acme-orders", "order.created", json, idempotencyKey: $"evt_{order.EventId}"));

// Later:
await service.CancelAsync(result.Record.Id);
```

Each request is a `POST` with these headers:

```
Content-Type: application/json
Idempotency-Key: evt_123                      (same on every attempt)
Webhook-Event: order.created
Webhook-Attempt: 1
Webhook-Timestamp: 1795800000                 (unix seconds, fresh per attempt)
Webhook-Signature: v1=<hex hmac-sha256>[, v1=<hex>]
```

Receivers verify the request with `WebhookSignatureVerifier`, or with any HMAC library by following the
algorithm in [docs/security.md](docs/security.md#signature-format).

## Run the sample

```sh
dotnet run --project samples/JspIt.WebhookDelivery.Sample
```

The sample starts a local receiver that verifies signatures, de-duplicates by key, and fails its first
request. It then enqueues events into a SQLite store and prints the retry and the final delivery summary.

## Security defaults

- **There is no open relay.** Messages can only target destinations configured up front, by id.
- **HTTPS only** by default, with TLS 1.2 or later and platform certificate validation. Validation cannot be turned off.
- **SSRF protection.** Resolved IPs are checked at connect time, so DNS rebinding does not get around the check. Loopback, private,
  link-local and cloud-metadata ranges are blocked unless explicitly allowed per destination.
- **Redirects are never followed.** No proxy is used, and cookies are not sent.
- **Bounded everything.** Timeouts, attempts, delays, delivery age, payload size and concurrency all have hard caps.
- **Log redaction.** Payloads, secrets, header values and URL paths or queries are never logged.
- **Signing is mandatory.** Multiple active keys allow key rotation without downtime.

The full threat model and boundaries are in [docs/security.md](docs/security.md).

## Documentation

- [Architecture](docs/architecture.md): components, delivery lifecycle, leasing and crash recovery
- [Delivery semantics](docs/delivery-semantics.md): at-least-once, duplicates, ordering, receiver guidance
- [Interfaces](docs/interfaces.md): public API and the `IDeliveryStore` contract for custom stores
- [Security](docs/security.md): boundaries, SSRF, TLS, signing, rotation, redaction
- [Versioning](docs/versioning.md): SemVer, wire format, schema versions
- [Dependencies and licensing](docs/dependencies.md)

## Building

```sh
dotnet build WebhookDeliveryKit.slnx
dotnet test --solution WebhookDeliveryKit.slnx
```

The tests start real HTTP endpoints on `127.0.0.1` and need no network access beyond loopback.

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
