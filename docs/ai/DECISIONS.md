# Stage 21 Decisions

## D21-01 — Zulip is the only chat server

RelayCove contains no server, proxy or BFF. It consumes public Zulip REST and event APIs. Reason: remove a redundant protocol/security/operations surface and keep Zulip authoritative.

## D21-02 — Thin in-house C# adapter

Zulip has no officially maintained C# client library. RelayCove implements a small `HttpClient` adapter against Zulip 12.1 OpenAPI and does not use obsolete third-party .NET SDKs.

## D21-03 — Windows-first .NET 10 MAUI

Stage 21 targets only `net10.0-windows10.0.19041.0`, unpackaged, self-contained `win-x64`. Mobile and Mac targets require later platform gates; Linux is unsupported.

## D21-04 — Server-authoritative cache

SQLite is an account-isolated plaintext cache under the current OS user, not a second business database. API keys stay in SecureStorage. Queue IDs, event cursors and outbox exist only in the current process.

## D21-05 — No automatic retry of message sends

Zulip `local_id` supports local echo but is not an idempotency key. Ambiguous POST outcomes are never automatically resent; the user explicitly chooses whether to resend recovered text.

## D21-06 — Raw Markdown presentation

The client requests `apply_markdown=false` and displays raw text without a WebView. Rich rendering, attachments and active edit/delete UX remain outside MVP.

## D21-07 — Cache locking on credential loss

Normal restart with a valid SecureStorage envelope can unlock offline cache. Explicit logout, credential corruption and 401 delete/ignore credentials and lock cached content until the same account reauthenticates.
