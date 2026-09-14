# Third-Party Notices

RelayCove is licensed under the MIT License. Its build uses third-party packages whose licenses remain with their respective owners; the authoritative package graphs are the restored NuGet assets and `src/RelayCove.Web/package-lock.json` for the released source revision.

## Zulip

RelayCove interoperates with a separately deployed Zulip Server through Zulip's documented public REST and event APIs. RelayCove does not redistribute Zulip server source code, official client source code, trademarks, logos or client artwork.

- Project: [Zulip](https://github.com/zulip/zulip)
- API documentation: [docs.zulip.com/api](https://docs.zulip.com/api/)
- License of the separately distributed Zulip project: Apache License 2.0

Zulip is a trademark of Zulip, Inc. This descriptive reference does not imply endorsement.

## Mattermost notification sound

RichChat includes the unmodified `bing.mp3` notification sound from Mattermost as
`Assets/Audio/mattermost_bing.mp3`.

- Copyright (c) 2015-present Mattermost, Inc. All Rights Reserved.
- Source: [Mattermost webapp sound](https://github.com/mattermost/mattermost/blob/94d6a203950611e96f58dc85c88b75461126c9c2/webapp/channels/src/sounds/bing.mp3)
- Revision: `94d6a203950611e96f58dc85c88b75461126c9c2`
- SHA-256: `0AFCB6B9473CAEB306B8C10D3E9A90F466F4012EF2A95C2D9F670AA9B25C32C5`
- License: Apache License 2.0; the full text is distributed at `Assets/Audio/Mattermost-LICENSE.txt`.
- The upstream [licensing policy](https://github.com/mattermost/mattermost/blob/94d6a203950611e96f58dc85c88b75461126c9c2/LICENSE.txt)
  applies Apache License 2.0 to `webapp/` and its subdirectories.

Mattermost is a trademark of Mattermost, Inc. This attribution identifies the
sound's source and does not imply endorsement.

## Direct runtime dependencies

- [.NET MAUI](https://github.com/dotnet/maui) — MIT License
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MIT License
- [Windows Community Toolkit Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) — MIT License
- [System.Drawing.Common](https://github.com/dotnet/winforms) — MIT License
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) — MIT License
- [SQLite / SQLitePCLRaw native e_sqlite3](https://www.sqlite.org/copyright.html) — SQLite public-domain dedication and package-specific notices
- [xUnit.net](https://github.com/xunit/xunit) — Apache License 2.0 (test-only)
- [React](https://github.com/facebook/react) — MIT License
- [Vite](https://github.com/vitejs/vite) — MIT License
- [Lucide](https://github.com/lucide-icons/lucide) — ISC License
- [Vitest](https://github.com/vitest-dev/vitest) — MIT License (test-only)
- [Playwright](https://github.com/microsoft/playwright) — Apache License 2.0 (test-only)

Before any public binary release, generate and archive an exact dependency/license inventory from the locked restore graph and include every notice required by the versions actually packaged.
