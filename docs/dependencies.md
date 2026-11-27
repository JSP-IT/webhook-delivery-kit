# Dependencies and license review

The project is licensed under **Apache-2.0**. This review covers every package that ships with the
libraries (runtime dependencies) and every package used only to build, test or demo them. The license of each
package was taken from its `.nuspec` license expression at the pinned version. Versions are pinned centrally in
`Directory.Packages.props`.

## Shipped (runtime) dependencies

| Package | Version | License | Used by | Notes |
| --- | --- | --- | --- | --- |
| Microsoft.Extensions.Logging.Abstractions | 10.0.15 | MIT | core | `ILogger` and source-generated log messages |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.15 | MIT | core (transitive) | Pulled in by Logging.Abstractions |
| Microsoft.Data.Sqlite / .Core | 10.0.15 | MIT | Sqlite | ADO.NET provider |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .provider.e_sqlite3, .lib.e_sqlite3 | 2.1.13 | Apache-2.0 | Sqlite (transitive) | Native SQLite binaries for each platform |
| SQLite engine (bundled in `lib.e_sqlite3`) | — | Public domain | Sqlite (transitive) | No obligations |

Everything else is part of the .NET 10 shared framework (MIT): `System.Net.Http`, `System.Security.Cryptography`
and `System.Text.Json`.

## Build, test and sample only (not shipped)

| Package | License |
| --- | --- |
| xunit.v3 (and its xunit.v3.* and Microsoft.Testing.Platform dependencies) | Apache-2.0 / MIT |
| Microsoft.Extensions.TimeProvider.Testing | MIT |
| Microsoft.AspNetCore.App (shared framework, used for local mock endpoints and the sample) | MIT |
| Microsoft.Extensions.Logging.Console (via the ASP.NET Core shared framework, sample only) | MIT |

## Compatibility assessment

- **MIT → Apache-2.0:** compatible. MIT code can be used in an Apache-2.0 project. Redistribution requires keeping
  the MIT copyright and permission notice. We don't vendor or modify any MIT source; NuGet packages are
  consumed as dependencies and carry their own license files.
- **Apache-2.0 → Apache-2.0:** compatible. The SQLitePCLRaw packages ship no `NOTICE` file that would need
  propagating. If a future version adds one, it must be reproduced in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
- **Public domain (SQLite):** no obligations.
- **No copyleft** (GPL, LGPL, AGPL, MPL or EPL) packages appear anywhere in the graph.
- **Patents:** Apache-2.0 includes an explicit patent grant from contributors. The MIT dependencies don't
  include one, which is normal for the .NET ecosystem and acceptable.

## Ongoing checks

CI runs `dotnet list package --vulnerable --include-transitive` and fails on any finding. When you add or upgrade a
dependency, update this file and THIRD-PARTY-NOTICES.md in the same pull request, and prefer packages that
are MIT, Apache-2.0, BSD or public domain. Any other license requires an explicit review.
