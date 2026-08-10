# RelayCove Agent Guide

## Read order

Before editing, read:

1. `RelayCove_Zulip_MAUI_重建开发计划.md` — product, architecture, security and acceptance source of truth.
2. `docs/ai/STATUS.md` — verified current state and open gates.
3. `docs/ai/WORKFLOW.md` — execution and evidence rules.
4. The active record under `docs/ai/tasks/`.

Repository evidence outranks official documentation; official Zulip 12.1/OpenAPI evidence outranks assumptions. Mark results as verified only after running the named command against the current tree.

## Architecture boundaries

- `RelayCove.App`: MAUI Views/ViewModels, Windows composition root and platform credential/config adapters.
- `RelayCove.Core`: domain models, reducer, use cases and public interfaces; no MAUI, JSON, HTTP or SQLite references.
- `RelayCove.Zulip.Client`: Zulip REST/event protocol and DTO mapping; no persistence or UI.
- `RelayCove.Data`: SQLite cache and migrations; no credentials or network calls.
- `RelayCove.Zulip.LiveTests` never runs as part of ordinary build/test commands.

Do not introduce a RelayCove server, proxy, BFF, obsolete Zulip .NET SDK, WebView message renderer, alternate credential file, SQLCipher, installer, updater, mobile target or frozen visual system in Stage 21.

## Code style

Use four-space indentation, file-scoped namespaces, nullable reference types, one public type per file, async I/O, cancellation tokens and deterministic tests. Public types/members use PascalCase, locals use camelCase, interfaces use `I`, and async methods end in `Async`. xUnit names follow `Method_WhenCondition_ExpectedResult`. Bug fixes require a regression test.

Secrets must never appear in `ToString`, exceptions, logs, snapshots, fixtures or packages. Production HTTP redirects stay disabled. Non-idempotent message sends are never automatically retried.

## Validation

During development:

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

Before a delivery commit:

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

`Live` requires explicit isolated test credentials and write confirmation; missing configuration must fail closed. Never substitute a personal account or production channel.

Inspect `git status` before and after work. Preserve unrelated user changes. Do not use destructive Git commands, force-push, push, tag, publish, alter the Zulip host, delete legacy local data or run external writes without explicit authorization.

Authentication, protocol, synchronization, data, outbox and packaging changes require an independent read-only review. Record unresolved P0/P1 findings and unverified VM/Live gates in STATUS rather than weakening the acceptance criteria.
