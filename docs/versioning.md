# Versioning

The project follows [Semantic Versioning 2.0](https://semver.org/). The version is set once, in
`Directory.Build.props` (`<Version>`), and applies to both packages, which are always released together
with the same version.

## Pre-1.0 (current: 0.1.0)

While the major version is `0`:

- **Minor** releases (`0.x.0`) **may contain breaking public API changes**. Each one is listed under
  "Breaking changes" in [CHANGELOG.md](../CHANGELOG.md).
- **Patch** releases (`0.x.y`) contain only backwards-compatible fixes.

Pin to a minor version (`[0.1.0, 0.2.0)`) until 1.0.

## What is versioned

There are four independently versioned surfaces. Changing one doesn't necessarily change the others.

| Surface | How it's versioned | Compatibility promise |
| --- | --- | --- |
| **.NET public API** | Package SemVer | As above. Package validation (`EnablePackageValidation`) runs on pack |
| **Wire format** (headers, signature) | Scheme prefix in `Webhook-Signature` (`v1=`) | `v1` is frozen. A new algorithm becomes `v2=` and can be sent alongside `v1=` during migration. Receivers ignore entries they don't understand |
| **Persisted enums** (`DeliveryStatus`, `AttemptOutcome`) and error categories | Fixed numeric and string values | Values are never renumbered or reused. New values may be added |
| **SQLite schema** | `wdk_schema.version` (currently `1`) | Upgrades migrate forward automatically on first use. A database with a newer schema than the library supports is refused rather than modified, so a downgrade fails safely |

## Supported frameworks

The packages target `net10.0` (the current LTS). The supported target frameworks follow the .NET support policy:
a framework may be dropped after it leaves Microsoft support, and that counts as a breaking change, so it only
happens in a minor (pre-1.0) or major release.

## Releasing

Releases are cut manually and are never published automatically from a merge:

1. Update `<Version>` in `Directory.Build.props`, `CHANGELOG.md`, and add `.github/release-notes-v<version>.md`.
2. Merge to `main` through a reviewed pull request.
3. Tag `v<version>` and run `dotnet pack -c Release -o artifacts`.
4. Publish `artifacts/*.nupkg` and `*.snupkg` to NuGet with a scoped API key. This is a manual step, and no publishing workflow is included.
