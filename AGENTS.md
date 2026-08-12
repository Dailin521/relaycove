# RelayCove Agent Guide

## Read order

Before editing, read:

1. `RelayCove_Zulip_MAUI_重建开发计划.md` — product, architecture, security and acceptance source of truth.
2. `docs/ai/STATUS.md` — verified current state and open gates.
3. `docs/ai/WORKFLOW.md` — execution and evidence rules.
4. The active record under `docs/ai/tasks/`.

Repository evidence outranks official documentation; official Zulip 12.1/OpenAPI evidence outranks assumptions. Mark results as verified only after running the named command against the current tree.

## Architecture boundaries

- `RelayCove.Web`: independently deployable TypeScript/React/Vite client; browser Zulip HTTP/session adapters and UI only, no MAUI runtime or .NET UI dependency.
- `RelayCove.App`: MAUI Views/ViewModels, Windows composition root and platform credential/config adapters.
- `RelayCove.Core`: domain models, reducer, use cases and public interfaces; no MAUI, JSON, HTTP or SQLite references.
- `RelayCove.Zulip.Client`: Zulip REST/event protocol and DTO mapping; no persistence or UI.
- `RelayCove.Data`: SQLite cache and migrations; no credentials or network calls.
- `RelayCove.Zulip.LiveTests` never runs as part of ordinary build/test commands.

The official Zulip Web remains untouched. `RelayCove.Web` and `RelayCove.App` both connect directly to the same Zulip Realm; never introduce a RelayCove server, proxy, BFF, second message backend, obsolete Zulip .NET SDK or WebView renderer. The two frontends share tokens, interaction specifications, capability matrices and acceptance scenarios, but not UI runtime code.

## Code style

Use four-space indentation, file-scoped namespaces, nullable reference types, one public type per file, async I/O, cancellation tokens and deterministic tests. Public types/members use PascalCase, locals use camelCase, interfaces use `I`, and async methods end in `Async`. xUnit names follow `Method_WhenCondition_ExpectedResult`. Bug fixes require a regression test.

Secrets must never appear in `ToString`, exceptions, logs, snapshots, fixtures or packages. Production HTTP redirects stay disabled. Non-idempotent message sends are never automatically retried.

Web users may opt to persist the API key in browser local storage and remember-login is the product default. Logout must remove persistent and session credentials; keys never enter URLs, logs, UI text or test snapshots.

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
