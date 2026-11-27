# Security

This document describes the security boundaries of the library, what it defends against, and what remains
the responsibility of the embedding application. To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Trust boundaries

| Party | Trusted for | Not trusted for |
| --- | --- | --- |
| **Embedding application** (configures destinations, enqueues messages) | Choosing destinations and their network opt-ins, protecting secrets and the database | Nothing is assumed beyond correct configuration. Message fields are still validated |
| **Message content** (event type, idempotency key, payload) | Nothing | It can never choose the URL, headers, or network policy |
| **DNS** | Nothing | Resolved addresses are checked at connect time |
| **Receiver** | Nothing | Its responses can't redirect us, set cookies, or make us store its body |
| **Delivery store** | Integrity of the delivery queue | It never holds signing secrets |

## No open relay

Messages target a **destination id**, and the URL comes only from configuration. There is no API that sends
to a caller-supplied URL, and enqueueing to an unknown id throws `UnknownDestinationException`. If a
destination is removed while deliveries are pending, those deliveries **fail** (`unknown_destination`).
They are never re-routed to another endpoint.

If your product lets end users register webhook URLs, the registration step is an application concern. Before
you build a `WebhookDestination` from user input, validate the URL, verify ownership if appropriate, and
never set `AllowLoopback` or `AllowedPrivateNetworks` on user-supplied destinations. The library's SSRF guard
still applies at connect time either way.

## SSRF protection

Each destination gets its own `SocketsHttpHandler`, and its `ConnectCallback` does the following:

1. It resolves the host. An IP literal is used as is.
2. It requires **every** resolved address to be allowed. A name that resolves to a mix of public and internal
   addresses is rejected outright.
3. It connects to the validated IP itself. A second DNS lookup cannot occur, which closes the DNS-rebinding window.

Addresses that are **blocked by default**:

- IPv4: `0.0.0.0/8`, `10/8`, `100.64/10` (CGNAT), `127/8`, `169.254/16` (link-local, including the cloud metadata address
  `169.254.169.254`), `172.16/12`, `192.0.0/24`, `192.0.2/24`, `192.88.99/24`, `192.168/16`, `198.18/15`,
  `198.51.100/24`, `203.0.113/24`, `224/4`, and `240/4`.
- IPv6: everything outside `2000::/3`, plus `::1`, `::`, `fc00::/7` (ULA, including `fd00:ec2::254`), `fe80::/10`, `fec0::/10`, `ff00::/8`,
  `2001::/23`, `2001:db8::/32`, `2002::/16` (6to4), and the NAT64 prefixes `64:ff9b::/96` and `64:ff9b:1::/48`.
- IPv4-mapped (`::ffff:a.b.c.d`) and IPv4-compatible IPv6 addresses are judged by their embedded IPv4 address.

There are two **explicit per-destination opt-ins**:

- `AllowLoopback`: permits `127/8` and `::1` only. Use it for local development, tests and sidecars.
- `AllowedPrivateNetworks`: permits specific CIDRs, such as an internal service. Ranges broader than `/8` (IPv4)
  or `/16` (IPv6) are rejected. Allowing loopback does not open private or metadata ranges.

Blocked connections fail the delivery permanently (`destination_blocked`). No request is sent.

### Redirects

**Redirects are never followed.** A `3xx` response is a permanent failure (`redirect_not_followed`).
Following one would re-send a signed payload to a location chosen by the receiver, possibly an internal address.

### Proxies

The handler does not use a proxy (`UseProxy = false`), so `HTTP(S)_PROXY` environment variables are ignored.
A forward proxy resolves DNS itself, which would bypass the address checks. If your environment requires an
egress proxy, enforce the equivalent destination policy at the proxy. Proxy support is not part of v0.1.0.

## Transport security

- **HTTPS is required.** Plain `http` needs `AllowInsecureHttp = true`, which is intended only for local development.
- **TLS 1.2 or 1.3 only.** Certificates are validated by the platform, and the library offers **no option to
  disable certificate validation**.
- URLs with user info (`user:pass@host`) are rejected, as are fragments. Put credentials in `Headers` instead (for example
  `Authorization`). Reserved headers (`Host`, any `Content-*`, `Transfer-Encoding`, `Cookie`, `Proxy-*`, `User-Agent`,
  `Idempotency-Key`, `X-Webhook-Signature`, `Webhook-*`) cannot be configured. Header values containing CR or LF are rejected.
- Cookies, automatic decompression and credential pre-authentication are disabled. Response headers are capped at 64 KiB.
  **The response body is never read or stored.**

## Bounded resource use

| Setting | Default | Hard bounds |
| --- | --- | --- |
| `Timeout` (per attempt, covers connect, TLS and response headers) | 10 s | 100 ms – 60 s |
| `MaxAttempts` | 8 | 1 – 50 |
| `InitialDelay` | 10 s | 100 ms – 1 h |
| `MaxDelay` | 1 h | ≥ `InitialDelay`, ≤ 24 h |
| `MaxDeliveryAge` | 24 h | 1 min – 7 days |
| `MaxPayloadBytes` | 256 KiB | 1 B – 10 MiB |
| `MaxConcurrency` / `BatchSize` | 8 / 32 | 1–256 / 1–500 |
| `LeaseDuration` | 2 min | ≥ max timeout + 5 s, ≤ 1 h |

The service validates all of these when it is constructed, and invalid values throw `WebhookConfigurationException`.

## Signature format

Signing is mandatory: every destination needs at least one key of at least 16 bytes (32 recommended,
`WebhookSigningKey.Generate`).

### Default scheme: `TimestampedHmacSha256V1`

```
signed_payload = "v1" LF timestamp LF idempotency_key LF event_type LF raw_body
signature      = lowercase_hex( HMAC-SHA256(secret, signed_payload) )

Webhook-Timestamp: <timestamp, unix seconds>
Webhook-Signature: v1=<signature>[, v1=<signature> ...]   (one entry per active key)
Idempotency-Key:   <idempotency_key>
Webhook-Event:     <event_type>
```

`LF` is a single `\n` byte. The idempotency key and event type are restricted to characters that can't
contain a newline, so the encoding is unambiguous. `Webhook-Attempt` is informational and **not** signed.

### Compatibility scheme: `BodyHmacSha256`

```
X-Webhook-Signature: sha256=<lowercase_hex( HMAC-SHA256(secret, raw_body) )>
```

This scheme is for receivers that already verify a body-only signature in this format. Use
`WebhookSigningKey.FromUtf8` if those receivers treat the secret as a string. This scheme has **no timestamp,
so captured requests can be replayed indefinitely**, and only the first active key signs, so rotation needs
coordination. Receivers must de-duplicate on `Idempotency-Key`. Prefer the default scheme for new integrations.

## Verifying requests

```csharp
var verifier = new WebhookSignatureVerifier([currentKey, previousKey]);   // tolerance defaults to 5 min
var status = verifier.Verify(name => request.Headers[name].FirstOrDefault(), rawBodyBytes);
if (status != WebhookVerificationStatus.Valid) return Results.Unauthorized();
```

When you implement verification yourself:

- Use the **raw body bytes** as received. Re-serialising parsed JSON changes the bytes.
- Compare signatures in **constant time**.
- Accept the request if **any** `v1=` entry matches **any** key you hold.
- Reject timestamps outside a tolerance window (5 minutes is reasonable). This limits replay. Within the
  window, de-duplication on `Idempotency-Key` handles the rest.

## Secret management and rotation

- **Secrets are never persisted by the library.** They live only in `WebhookDestination` configuration.
  Requests are signed at send time, so rotation also applies to deliveries enqueued before the rotation.
- `WebhookSigningKey.ToString()` never prints secret material, and the secret bytes are copied and kept internal.
- Load secrets from a secret manager (Key Vault, AWS Secrets Manager, or Kubernetes secrets). Don't keep them in source or plain config.

To rotate a key without downtime:

1. **Add** the new key next to the old one: `SigningKeys = [oldKey, newKey]`. Every request now carries two
   `v1=` signatures. Apply the change at runtime with `service.UpsertDestination(...)`, or by restarting.
2. **Distribute** the new key to the receiver, which then accepts either key.
3. **Remove** the old key from the sender, and later from the receiver.

You can also schedule the window ahead of time with `NotBefore` and `NotAfter` on each key. Keys outside their
window don't sign. If no key is active, attempts are retried with the `no_active_signing_key` error category
until a key becomes active or the delivery's retries or age run out.

## Log redaction and stored error text

The library **never logs** payloads, signing secrets, signatures, header values (including any
`Authorization` you configure), response bodies, or URL paths and query strings. Webhook URLs often embed
tokens, for example `/services/T000/B000/XXXX`. Logs contain the delivery id, destination id,
`scheme://host:port`, attempt number, status code and error category.

Exception text stored in `LastError` or attempt history, and written to logs, is passed through
`Redaction.ErrorMessage`. That function removes the destination URL, path and query, replaces control characters
(to prevent log injection) and truncates the text to 500 characters.

`ToString()` on `WebhookDestination`, `WebhookMessage` and `DeliveryRecord` omits URL paths and payloads.

## Data at rest

The store persists **payloads** in plain form so that deliveries survive restarts. Protect the database
file with filesystem permissions, use disk encryption where your data requires it, and schedule
`PurgeAsync` to delete terminal deliveries after your retention period. Don't put data in webhook
payloads that the receiver isn't entitled to.

## Out of scope

The library does not provide the following:

- Authentication of the *application* calling `EnqueueAsync`. That is in-process trust.
- Receiver-side de-duplication storage. The library supplies the key, and the receiver stores it.
- Payload encryption. Use TLS, and encrypt fields at the application level if needed.
- Mutual TLS client certificates. This is a candidate for a later release.
