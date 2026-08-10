# Third-Party Notices

RelayCove is licensed under the MIT License. Its build uses third-party packages whose licenses remain with their respective owners; the authoritative package graph and license metadata are the restored NuGet assets for the released source revision.

## Zulip

RelayCove interoperates with a separately deployed Zulip Server through Zulip's documented public REST and event APIs. RelayCove does not redistribute Zulip server source code, official client source code, trademarks, logos or client artwork.

- Project: [Zulip](https://github.com/zulip/zulip)
- API documentation: [docs.zulip.com/api](https://docs.zulip.com/api/)
- License of the separately distributed Zulip project: Apache License 2.0

Zulip is a trademark of Zulip, Inc. This descriptive reference does not imply endorsement.

## Direct runtime dependencies

- [.NET MAUI](https://github.com/dotnet/maui) — MIT License
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MIT License
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) — MIT License
- [SQLite / SQLitePCLRaw native e_sqlite3](https://www.sqlite.org/copyright.html) — SQLite public-domain dedication and package-specific notices
- [xUnit.net](https://github.com/xunit/xunit) — Apache License 2.0 (test-only)

Before any public binary release, generate and archive an exact dependency/license inventory from the locked restore graph and include every notice required by the versions actually packaged.
