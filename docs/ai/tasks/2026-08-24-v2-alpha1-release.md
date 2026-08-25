# RelayCove 2.0.0-alpha.1 — first MAUI prerelease

Status: completed; GitHub prerelease published 2026-08-25

## Purpose

- Close the first MAUI development cycle after Stage 30.
- Publish the repository's existing `2.0.0-alpha.1` version as a Windows 11 x64 GitHub prerelease.
- Keep the historical `v1.0.0-rc.25` release/tag intact.

## Release scope

- One-to-one/self private messages and supported private group chats in the unified native conversation list.
- Zulip-authoritative history, realtime events, unread state, attachments, reactions, own-message edit/delete, saved messages, quoting and unified search.
- Complete Zulip 12.1 Unicode emoji catalog, native Windows notifications/taskbar state, local SQLite cache and account-isolated credentials.
- Native group creation/settings/member management within the implemented permission boundary.

## Distribution

- Tag: `v2.0.0-alpha.1`.
- Asset: `RelayCove-2.0.0-alpha.1-win-x64.zip`.
- Integrity manifest: `RelayCove-2.0.0-alpha.1-win-x64.sha256`.
- The ZIP is unpackaged, self-contained, unsigned and has no automatic updater.

## Known boundaries

- Windows 11 x64 only.
- No installer, MSIX, code signing or background push; SmartScreen may warn.
- Final clean Windows 11 VM startup and the formal final password-login gate remain open.
- No Live suite or production Realm write is part of this release operation. Historical Stage 23 Live evidence is not rerun.
- `RelayCove.Web` is retained as historical source but is not included in the MAUI release asset.

## Verification evidence

- `pwsh ./scripts/verify.ps1 -Mode Full` stopped before build/tests at the repository-wide `dotnet format --verify-no-changes` gate. The output reports the pre-existing LF/CRLF normalization debt across many files, whitespace in `ClientSession.cs`/`TaskbarUnreadOverlay.cs`, and one import-order issue in `ComposerEditorHandler.cs`. These unrelated files were not batch-formatted for the release.
- The remaining Full steps were run explicitly without Live or Realm access. Debug solution build passed with 0 warnings/errors; Debug tests passed Core 151/151, Zulip.Client 102/102, Data 34/34 and App 268/268.
- Historical Web checks passed: deployment-tool templates, typecheck, 86/86 unit tests and the fixed-subpath production build. Local fake-HTTP Playwright passed 6/6 plus the deployment-subpath test 1/1.
- The first Release build exposed two strict XamlC binding errors that Debug had not rejected: the subscriber-row remove gate resolved against `ChannelMemberItem`, and the conversation empty view resolved through an invalid `BindingContext` path. Both bindings were corrected without changing commands or Realm behavior, and structural regressions were added.
- Final Release solution build passed with 0 errors and 7 compiled-binding performance warnings. Release tests passed Core 151/151, Zulip.Client 102/102, Data 34/34 and App 268/268.
- The pre-commit package trial contained all required application/runtime/license files and passed runtime-data/publish-text scans, but it was not released because its ProductVersion still referenced the preceding `57c8145` commit. The final ZIP was rebuilt from the committed candidate; its ProductVersion is `2.0.0-alpha.1+83eccf04a114defc9fc479f447d30709fb2d3807`, matching the commit targeted by the annotated tag.
- The final publish tree and ZIP each contain 661 files. Required executable/runtime/license entries are present; file paths, sizes and per-file SHA-256 values match between the publish tree and ZIP. Runtime-data, credential-pattern and unsafe/duplicate ZIP-entry scans passed.
- The final ZIP is 96,496,867 bytes with SHA-256 `8ED67737368D6C14FE7168F3E9A34DEB0E403EA8DE82DB47ED86F6005C8DCAD3`; the uploaded GitHub asset reports the same digest.
- Live, a production Realm write, Agent app startup, installer/signing and clean-VM execution were not run.

## Publication evidence

- Independent release-boundary review found no P0/P1/P2 after the clean rebuild. It confirmed that `HEAD`, ProductVersion, ZIP contents, manifest, release notes and prerelease boundaries all identify the same candidate and contain no tracked-worktree drift.
- Release candidate: `83eccf04a114defc9fc479f447d30709fb2d3807` (`chore: prepare first MAUI prerelease`).
- Annotated tag: `v2.0.0-alpha.1`; remote peeled tag target: `83eccf04a114defc9fc479f447d30709fb2d3807`.
- GitHub prerelease: https://github.com/Dailin521/relaycove/releases/tag/v2.0.0-alpha.1. It is published, not a draft, and contains the ZIP plus SHA-256 manifest.
- `origin/main`, the remote tag and both uploaded assets were verified after publication. This evidence-only documentation update intentionally follows the tagged binary-source commit.
