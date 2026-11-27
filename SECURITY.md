# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through GitHub's
[private vulnerability reporting](https://github.com/JSP-IT/webhook-delivery-kit/security/advisories/new)
for this repository. Include affected versions, a description, and reproduction steps if possible.

We aim to acknowledge reports within 5 business days and will coordinate a fix and disclosure timeline with you.

## Supported versions

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |

## Scope

Issues of particular interest include: bypasses of the SSRF/network policy, redirect following, TLS
downgrade or certificate-validation bypass, secrets or URL paths appearing in logs or stored errors,
signature forgery or verification bypass, and unbounded resource use. The design and its stated boundaries
are documented in [docs/security.md](docs/security.md).
