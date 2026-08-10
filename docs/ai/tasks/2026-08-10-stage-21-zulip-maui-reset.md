# Stage 21 — Zulip MAUI Reset

Status: in progress
Execution branch: local `main` by explicit user instruction
No push/server write authorized

## Objective

Replace the legacy WPF + custom server product with a clean Windows-first .NET 10 MAUI client that connects directly to Zulip 12.1/FL500, while preserving only the original icon and Git rollback history.

## Acceptance

- Clean four-layer solution and five isolated test projects.
- Password-to-API-key login, SecureStorage restore/logout and 401 lockout.
- Register/events/history/topics/send/read adapter with forward-compatible mapping.
- Per-account SQLite WAL cache, transactional reducer and authorization cleanup.
- Cache-first session, queue rebuild, pagination, local echo and no automatic resend.
- Minimal functional MAUI shell.
- Rewritten README, source-of-truth plan and governance docs.
- Offline-safe Fast, package-producing Full and fail-closed explicit Live modes.
- Independent protocol/security/data/sync/package review.
- Final ZIP and SHA-256; clean Windows VM and isolated Live evidence before completion.

## Evidence to date

- Old baseline: 1733/1733 tests passed before deletion.
- Workload/spike: self-contained unpackaged publish, SecureStorage and SQLite passed on developer machine.
- Full: zero Debug/Release build warnings; Core 79/79, Zulip.Client 29/29, Data 15/15, App 12/12 (135/135 total).
- Package: `RelayCove-2.0.0-alpha.1-win-x64.zip`, 93,615,535 bytes, 556 entries, SHA-256 `E802A55597CF889A961AB3FE7606492A65A47B61EDAD803CEA2373ACEA47FE56`.
- Package secret/content scan and SHA manifest verification passed; developer-machine five-second startup smoke passed.
- NuGet direct/transitive vulnerability audit passed after pinning `SQLitePCLRaw.bundle_e_sqlite3` to `2.1.12`; the loaded native SQLite security-floor regression test passed.
- Live mode fail-closed behavior verified without credentials; no external write occurred.
- Independent code/package review completed with no reproducible P0/P1; a prior protocol concern was rejected using the pinned Zulip 12.1 OpenAPI because long-poll and idle-queue timeouts have distinct semantics.
- Icon hash preserved: `07906CE7D87860C4A15DDD6F904DA722F7BBC3C882DC32FD1D285A78B1161B52`.

## Open gates

- Dedicated-account Live run, manual password login and clean Windows 11 VM launch.
- Push, tag, upload and public release remain outside current authorization.

Do not change this task to complete until every external acceptance gate has evidence.
