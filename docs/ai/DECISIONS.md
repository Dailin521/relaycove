# RelayCove Architecture Decisions

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

## D21-08 — UI design-first, native implementation

Superseded for future UI delivery by D22-01 through D22-04. The frozen `chat-ui-v1` HTML and screenshots remain immutable design evidence; they are not embedded in either product runtime.

## D22-01 — Two first-class frontends

RelayCove.Web is an independently deployable product, while RelayCove.App remains a native .NET MAUI product. The official Zulip Web is retained unchanged. Both RelayCove frontends connect directly to the same Zulip Realm, and no RelayCove server, BFF, proxy protocol or second message backend is introduced.

## D22-02 — Web first, then native parity

Interaction work lands and is accepted in RelayCove.Web first. A versioned interaction contract is then frozen and reproduced natively in MAUI without WebView. The two products share visual tokens, interaction specifications, capability matrices and acceptance scenarios, but no UI runtime code.

## D22-03 — Browser-local credentials are an explicit product choice

RelayCove.Web defaults to remember-login and may store Realm, email and API key in browser local storage for private-realm convenience. Turning remember-login off uses session storage. Logout clears both; secrets never enter URLs, logs, UI text or test snapshots. This browser XSS/storage risk is accepted and documented separately from MAUI SecureStorage.

## D22-04 — Fixtures and live Zulip state are separate

Deterministic Web visual data lives only under the development fixture boundary and is excluded from the production build. Formal API tests use fake HTTP. A fixture must never be treated as a Zulip snapshot or shared with the production data path.

## D22-05 — Local daily loop, versioned server acceptance

Daily Web implementation and fast visual checks run locally through the deterministic fixture. Deliberate large-version manual acceptance uses the fixed same-origin `https://hklight.2000521.xyz/relaycove-web/` static entrance and a one-click verified atomic deployment. The official Zulip root and legacy `/relaycove/` routes remain unchanged. Server synchronization is explicit, not deploy-on-save.
