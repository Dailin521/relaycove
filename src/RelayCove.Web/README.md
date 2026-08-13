# RelayCove.Web

`RelayCove.Web` is RelayCove's independently deployable React client. It connects directly from the browser to a Zulip Realm and does not require a RelayCove server, BFF or proxy.

## Local setup

Dependency/bootstrap commands are explicit networked setup steps; repository Fast/Full never run them automatically:

```powershell
npm ci
npx playwright install chromium
```

Development and narrow verification:

```powershell
npm run dev
npm run typecheck
npm run test:unit
npm run build
npm run test:e2e
```

The production build is written to `dist/`. Playwright uses `dist-e2e/`, a local preview at `127.0.0.1:4173`, fake HTTP and the E2E-only `?fixture=chat` route. Vite resolves a fixture-aware runtime only for explicit `fixture`/`e2e` modes; ordinary development and production use `App.tsx`, and the production graph contains no fixture module, account or payload.

Production builds are rooted at the fixed `/relaycove-web/` path. The accepted large-version preview is published at `https://hklight.2000521.xyz/relaycove-web/`; the existing official Zulip root site and legacy `/relaycove/` service remain untouched.

For daily local work, double-click `start-web-dev.cmd` at the repository root. It starts Vite, waits for the local page and opens the formal real-Realm client at `http://127.0.0.1:5173/`. Fixtures are automation-only. For deliberate large-version manual acceptance, double-click `deploy-web.cmd`; it runs the full Web verification suite before a versioned atomic deployment. This is not an automatic deploy-on-save workflow.

## Boundaries

- `src/api/`: canonical Realm handling, login exchange, authenticated Zulip REST/event methods and defensive DTO mapping.
- `src/domain/`: canonical channel-topic/DM identities and normalized Web client contracts.
- `src/state/`: pure reducer and React-subscribable store for users, subscriptions, topics, messages, unread, message mutations and outbox.
- `src/session/`: browser credential/preferences persistence plus lifecycle, register, long-poll, paging, read, send and queue-rebuild orchestration.
- `src/components/`: React UI runtime; it does not call Zulip or browser storage directly.
- `src/workspace/`: normalized state-to-UI projection; it does not receive API keys or raw Zulip DTOs.
- `src/fixtures/`: deterministic visual data, never a formal Zulip state source.
- `src/styles/tokens.css`: shared visual values maintained independently from MAUI runtime resources.

The formal production path implements `/users/me`, `/register`, `/events`, channel topics, 1:1/group/self-DM, known contacts, 50-message history paging, server-confirmed read flags, serialized text/attachment send, local-echo/read-only reconciliation, outbox recovery, reconnect and bad-queue rebuild. Message actions, protected same-Realm avatars, controlled image previews, non-image file downloads and per-conversation attachment drafts use the same production state path. Queue/cursor, messages, media Blob URLs and outbox are intentionally page-memory only; refresh-offline history is not claimed.

Implemented writes include message send, multi-file upload links, reaction add/remove, own-message edit/delete, per-account starred flags and current-user channel unsubscribe. Channel and DM groups collapse independently and persist as non-sensitive preferences. Still separate capability gates: global/server search, mention candidates, the saved-message list, presence/notifications, reliable channel membership, channel subscribe/create/rename/archive/member management and resumable large-file upload.

Media safety limits are explicit: only canonical same-Realm avatar/upload paths are accepted; inline previews are PNG/JPEG/WebP/GIF/AVIF only; SVG/HTML/PDF/Office/archive files are never embedded and use a download card. One message renders at most four images and ten total attachment cards; the shared loader allows four concurrent requests and keeps at most 64 MiB of Blob data. Composer permits ten files, applies per-file and aggregate budgets, uploads in deterministic order and never retries an ambiguous upload/message write. Logout aborts in-flight upload work and invalidates late adapter results.

## Browser credentials

Remember-login is checked by default. When checked, Realm, email and API key are stored in this origin's `localStorage`; otherwise they use `sessionStorage`. Logout clears the credential key from both stores; non-sensitive appearance preferences may remain. Password is never persisted. API keys must never be rendered, logged, placed in URLs or included in snapshots.

This is an explicit private-realm convenience tradeoff, not an equivalent of MAUI SecureStorage. The fixed deployment is a verified same-origin HTTPS path with CSP (including `frame-ancestors`), HSTS, no-referrer, nosniff and cache headers. A future origin change must reopen the Zulip CORS gate; do not add a proxy to bypass it.
