# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/)
(see [docs/versioning.md](docs/versioning.md)).

## [0.1.0] - Unreleased

Initial MVP.

### Added
- `WebhookDeliveryService`: enqueue, get, cancel, attempt history, batch (`ProcessDueAsync`) and loop (`RunAsync`) dispatch,
  runtime `UpsertDestination`.
- At-least-once delivery with persisted delivery records, leases with fencing tokens and crash recovery.
- Idempotent enqueue per destination and idempotency key, with conflict detection for reused keys.
- Bounded exponential backoff with jitter, `Retry-After` support, attempt and age limits.
- HMAC-SHA256 signing (`TimestampedHmacSha256V1`, plus `BodyHmacSha256` compatibility mode), multi-key rotation,
  and `WebhookSignatureVerifier` for receivers.
- SSRF protection at connect time, no redirects, no proxy, HTTPS and TLS 1.2+ by default, bounded timeouts and payload sizes.
- Log and error-text redaction.
- `IDeliveryStore` persistence interface, `InMemoryDeliveryStore`, and the `JspIt.WebhookDelivery.Sqlite` reference store.
- Runnable sample, architecture/security/semantics/interfaces/versioning documentation, CI.
