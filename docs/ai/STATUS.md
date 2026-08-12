# RelayCove Stage 21 Status

Updated: 2026-08-12
Branch: `main` by explicit user direction; the 2026-08-12 UI documentation delivery is authorized to commit and push
Target: `2.0.0-alpha.1`, Windows x64, Zulip-only MAUI client

## Verified

- Original HEAD `46c9f0e74068a29f4fb14180f426f1fbf30cef36`; rollback tag `v1.0.0-rc.25` exists.
- Old Fast baseline passed 1733/1733 tests before deletion.
- Old WPF/server/shared/updater/tests/installer/product docs are removed through ordinary Git history.
- Only original product asset retained: `RelayCove.ico`, 65,044 bytes, SHA-256 `07906CE7D87860C4A15DDD6F904DA722F7BBC3C882DC32FD1D285A78B1161B52`.
- .NET SDK 10.0.101 and `maui-windows` workload installed.
- Repository-external MAUI spike passed unpackaged self-contained `win-x64` publish, SecureStorage round-trip, SQLite WAL/transaction and native `e_sqlite3.dll` checks on this development machine.
- `pwsh ./scripts/verify.ps1 -Mode Full` passed on the current tree with zero Debug/Release build warnings and 135/135 local tests: Core 79, Zulip.Client 29, Data 15, App 12.
- Full published only `src/RelayCove.App/RelayCove.App.csproj` as unpackaged, self-contained `win-x64`; required `RelayCove.App.exe`, `coreclr.dll` and `e_sqlite3.dll` are present.
- 2026-08-10 package snapshot: `artifacts/package/RelayCove-2.0.0-alpha.1-win-x64.zip`, 93,615,535 bytes, 556 entries, SHA-256 `E802A55597CF889A961AB3FE7606492A65A47B61EDAD803CEA2373ACEA47FE56`.
- ZIP manifest hash matches; `LICENSE` and `THIRD-PARTY-NOTICES.md` are present; content and secret scans found no database, log, environment file or configured Live secret value.
- The packaged executable remained running for a five-second startup smoke check on this development machine.
- `SQLitePCLRaw.bundle_e_sqlite3` is pinned to `2.1.12`; the loaded native SQLite security floor test passed, and NuGet found no known direct or transitive package vulnerability in any solution project.
- Live mode was invoked without credentials and failed closed at the first missing required variable; no external write occurred.
- Independent final review found no reproducible P0/P1. A prior timeout-field concern was checked against the Zulip 12.1 OpenAPI: `event_queue_longpoll_timeout_seconds` is the `GET /events` HTTP timeout, while `idle_queue_timeout_secs` controls server-side queue collection.
- Target public server settings previously verified as Zulip 12.1 / feature level 500 / email auth enabled.
- User-approved Web UI baseline `docs/ui/baselines/chat-ui-v1/RelayCove-UI-Playground.html` is frozen at SHA-256 `6C97FD26773F4D83A8DB76FFE2A61A6D0BFF9947AAD9E4533AD8B7DE3810507E`. Layout was checked at 1440×900 and 1024×768 browser viewports; the captured application-region PNG dimensions are recorded in its manifest.
- Browser regression verified message search excludes time/date/unread separators, includes attachment filenames, unread badges recompute from conversation state, composer pointer/keyboard resize works, and console reported zero errors/warnings.
- On 2026-08-12, `pwsh ./scripts/verify.ps1 -Mode Fast` and `-Mode Full` passed for the UI freeze tree with zero build warnings/errors and 135/135 local tests in both Debug and Release. Full regenerated only ignored local package artifacts; no package publication occurred.

## Local implementation state

- Four-layer implementation, minimal functional shell, offline tests, Full packaging and local review are complete.
- Stage 21 remains in progress because the external acceptance gates below are intentionally unverified.
- The approved Chat UI is documented in `docs/ui/INTERACTION_SPEC.md`; `docs/ai/tasks/2026-08-12-stage-22-native-chat-ui.md` is planned, not active, and no MAUI visual conversion was implemented in this documentation freeze.

## Unverified / blocked by external evidence

- Final ZIP launch on a clean Windows 11 x64 VM without .NET or Windows App SDK Runtime.
- Live contract/write test using two dedicated test accounts and isolated private channel.
- One manual password-login UI acceptance using a dedicated account.
- Native MAUI visual implementation/acceptance, signing, installer and public release are outside Stage 21. Search, attachments, reactions, mention candidates and channel management remain separate capability gates.

Do not mark Stage 21 complete until explicit Live, clean-VM and manual password-login evidence also exist.
