# RelayCove Status — Stage 21 / 22W / 22M

Updated: 2026-08-12
Branch: `codex/stage-22w-web-foundation`
HEAD: `1374985197aca39806b8780f2e9f17799ffe3abe`
Current delivery: Stage 22W / Slice 1 foundation, Slice 2 formal Zulip message client and Slice 3 message actions/avatar/image capability implemented, independently reviewed, locally verified and deployed to the fixed Web entrance; uncommitted for user review

## Product direction

- The official Zulip Web remains unchanged.
- `RelayCove.Web` is an independently deployable formal client; `RelayCove.App` remains native .NET MAUI without WebView.
- Both frontends connect directly to the same Zulip Realm. Zulip remains the only business source of truth; there is no RelayCove server, BFF, proxy protocol or second message backend.
- Web is implemented and accepted first. A versioned interaction contract then becomes the input for Stage 22M native parity.
- The two frontends share visual tokens, interaction specifications, capability matrices and acceptance scenarios, but no UI runtime code.

## Stage 22W foundation, formal message client and first complete interaction capability

- Starting state was clean `main` at `1374985`; all four `docs/ui/baselines/chat-ui-v1/` SHA-256 values were recomputed and matched before work.
- Local toolchain: Node `v24.11.1`, npm `11.10.0`, repository Playwright `1.62.1`; the locked Web stack is TypeScript 7, React 19 and Vite 8.
- `src/RelayCove.Web/` contains separate application/auth routing, product/window bar, primary navigation, channel/DM/topic/contact list, chat header/message list, multiline Composer, collapsible details and settings components. Visual tokens are isolated under `src/styles/tokens.css`.
- Light/dark, 1440×900, 1024×768 and below-720 single-pane layouts are implemented. Composer pointer/keyboard sizing is clamped to 72–300 px and drafts are keyed by conversation.
- Deterministic visual data is confined to `src/fixtures/`. Vite selects the fixture-aware entry only for development/E2E; the production module graph and bundle exclude fixture accounts, labels and chat payloads.
- Icons and dependencies are bundled locally. Production `dist/index.html` has no runtime CDN reference; the current reviewed build is `295.77 kB` JavaScript (`90.61 kB` gzip) and `29.88 kB` CSS (`6.43 kB` gzip).
- Production builds are rooted at `/relaycove-web/`, include a bundled favicon and are covered by a strict-prefix deployment preview: missing hashed assets return 404 while client routes remain inside the same prefix.
- Browser API/session boundary implements public `GET /api/v1/server_settings`, password exchange through `POST /api/v1/fetch_api_key`, authoritative `GET /users/me`, register/events/topics/history/mark-read/send/delete-queue and HTTP Basic from email + API key. Realm accepts only a canonical HTTPS origin.
- Remember-login defaults to enabled. Checked sessions use local storage, unchecked sessions use session storage, corrupted data fails closed, and confirmed logout clears both. Passwords are never persisted; API keys do not enter URLs, rendered UI, error bodies or test snapshots.
- HTTP requests force `cache: no-store`, `credentials: omit`, `redirect: error` and `referrerPolicy: no-referrer`; authenticated callers cannot weaken these defaults.
- `WebClientSession` keeps queue/cursor/lifecycle/outbox in memory, applies register and realtime patches through a pure reducer/store, cancels stale history, rebuilds expired queues, respects server-confirmed unread, serializes text sends, and never auto-retries an ambiguous non-idempotent POST.
- Production UI projects subscribed channel topics, known contacts, 1:1/group/self-DM, 50-message raw Markdown pages, connection/outbox states and settings without exposing raw Zulip DTOs or credentials to components.
- Slice 3 adds one complete interaction boundary: right-click/keyboard/mobile message menus; protected same-Realm avatars with initials/Bot fallback; controlled image thumbnail/download/viewer; and Composer image validation, per-conversation draft, one upload plus one Markdown send.
- Realm media is HTTPS/origin/path allowlisted, carries no API Key in URL/DOM, caps previews at 4 per message, loads at most 4 concurrently and retains at most 64 MiB of Blob data. Logout synchronously aborts uploads, revokes draft URLs and invalidates late results before session/credential cleanup.
- `npm audit --omit=dev --audit-level=high` reported `0 vulnerabilities` on the locked tree.

## Current repository verification evidence

- Final `pwsh ./scripts/verify.ps1 -Mode Fast` passed: zero .NET build warnings/errors; 135/135 Debug tests (Core 79, Zulip.Client 29, Data 15, App 12); Web deployment-tool regression, typecheck, 63/63 unit tests, production build, subpath/favicon, CDN and fixture-exclusion scans.
- Final `pwsh ./scripts/verify.ps1 -Mode Full` passed on the final text/code tree: Fast repeated; 135/135 Release tests; app-project-only Windows publish/package; Web Playwright 6/6 fixture/formal scenarios plus 1/1 production deployment-path scenario.
- Fast/Full require explicitly pre-provisioned NuGet/npm/Chromium assets and use no `.NET restore`, package install, real credential or target-Realm request. Playwright serves only `127.0.0.1` and intercepts the fake Zulip origin.
- Playwright covered mocked login/restore/logout, console errors and warnings, keyboard focus, Composer clamp, details Escape, 640 px list/chat switching and both list/chat horizontal-overflow checks.
- Windows package: `artifacts/package/RelayCove-2.0.0-alpha.1-win-x64.zip`, 93,615,658 bytes, SHA-256 `362D5D19995DA0CD5ED933641B383DD266B24077C477DABAA3A9E127AA932A0F`. It was generated locally and not published.

## Slice 2 historical evidence

- `npm run typecheck`: passed.
- `npm run test:unit`: 48/48 passed across 9 files. Coverage includes identity mismatch/no-persist, preservation of the `fetch_api_key` authentication email when `users/me` exposes a different profile address, register/event/history/topic/read/send/queue request contracts, canonical self/group DM, authorization replacement, authoritative unread/revocation cleanup, ambiguous and hanging send one-attempt recovery, bad-queue/restart rebuild with jitter, mark-read failure preservation and stop/start lifecycle races.
- `npm run test:e2e`: formal fake-HTTP journey plus fixture UI/responsive/visual scenarios 5/5; production `/relaycove-web/` path preview 1/1. The formal journey covers login → users/me → register → DM/topic history → read confirmation → group DM → exactly one text send/read-only reconciliation → refresh restore → queue delete/logout.
- Current production output before gzip: JavaScript `272.68 kB` (`83.82 kB` gzip), CSS `24.22 kB` (`5.47 kB` gzip); fixture imports/markers remain absent.
- The final repository Fast and Full commands both passed on the hotfix tree. The versioned server sync then reran Web typecheck, 48/48 unit tests, production build and both Playwright suites before changing the server symlink.

## Current Slice 3 local evidence

- `npm run typecheck`: passed.
- `npm run test:unit`: 63/63 passed across 14 files. New regressions cover same-Realm media URLs, MIME/size/temporary URL boundaries, four-preview extraction, four-way loader concurrency, avatar fallback, image selection, upload forms, and logout-during-upload with a late adapter success that must never send.
- `npm run test:e2e`: 6/6 passed plus production `/relaycove-web/` preview 1/1. The formal fake-HTTP journey covers protected avatars, temporary upload URL with no Basic header on the final Blob read, image modal/download/focus, right-click and `Shift+F10`, clipboard, invalid SVG, remove-with-text-preservation, exactly one multipart upload and exactly one image Markdown message POST.
- Production output before gzip: JavaScript `295.77 kB` (`90.61 kB` gzip), CSS `29.88 kB` (`6.43 kB` gzip); fixture imports/markers remain absent.
- Final repository Fast and Full both passed after all P1 fixes and the visible login-copy correction. The final deployment reran Web typecheck, 63/63 unit tests, production build and both Playwright suites before changing the server symlink.

### Browser screenshots

| Evidence | Dimensions | SHA-256 |
|---|---:|---|
| `artifacts/web/screenshots/desktop-1440-light.png` | 1440×900 | `79521F88CCFF1B4F554F6679BE268C02D6E830A1A8CE4E66F669A5FA15D23BBE` |
| `artifacts/web/screenshots/desktop-1440-dark.png` | 1440×900 | `2DA96C1B21F4BBDAA3618BCD937E30E73BBD470DF438EBE190C234D850B2B3F6` |
| `artifacts/web/screenshots/desktop-1024-light.png` | 1024×768 | `4C8576A2B2C12FA7BFFCFCF923250047AC913854FCBE3557AD54F48A0A0A92D8` |
| `artifacts/web/screenshots/formal-client-fake-1440-light.png` | 1440×900 | `B96CF55028FCA4E0BD7F22CA42F2E567CB8C158C1D0EDF668FB8630EBD5C7E01` |
| `artifacts/web/screenshots/message-actions-1024-light.png` | 1024×768 | `E723057A4B3329C7C0F5389BC73B16825A5F50DBF22E80C55F22AE13F1140380` |
| `artifacts/web/screenshots/image-preview-1024-light.png` | 1024×768 | `149E187904EC50867206BA4E352454FEE258679677E8E233E0E10F4FFD93B884` |
| `artifacts/web/screenshots/composer-image-1440-light.png` | 1440×900 | `5A6A48B8FB3BF9136811EDE12E7AC1E9F2BAD98EA01A934BD2D2A52FDC3F0316` |
| `output/playwright/relaycove-web-slice3-public-login-1440x900.png` | 1440×900 | `A5E8C5FC40EEF3495B31DEF32298D9F13E876F15C17A8F6F51A1A996938AC21C` |

The HTML Playwright report is under `artifacts/web/playwright/report/`. All generated Web evidence is ignored by Git.

## Independent review

- Independent read-only review tracks covered authentication/HTTP safety, protocol/session/outbox behavior, state/UI projection, tests/documentation/frozen-baseline integrity and deployment/Nginx/release safety.
- Confirmed P1 findings were fixed in their owning layers: authenticated request defaults are non-overridable; Fast/Full perform no restore; fixture code is excluded from production; unread/revocation state stays authoritative; ambiguous sends never auto-resend; local IDs are increasing numeric strings; every reconnect path is jittered; logout waits for sends; and stop/start cannot retain or overwrite another lifecycle's outbox/state.
- Every affected narrow suite was rerun, followed by final Fast and Full. All reviewers reported no remaining P0/P1.
- After a user-reported immediate logout, an additional independent authentication review confirmed the hotfix has no P0/P1: Basic authentication now always preserves the canonical email returned by `fetch_api_key`; `users/me.email` remains profile data and cannot replace the credential username.
- Slice 3 independent protocol/session/UI reviews confirmed and closed two P1 findings: logout now cancels and invalidates an in-flight image submission before clearing the session, and attacker-controlled messages can no longer trigger unbounded image reads. The original reviewers rechecked both fixes and reported no remaining P0/P1. UI review reported only non-blocking P2 coverage additions; account-menu keyboard navigation and avatar-failure regression were also added in this Slice.

## Authorized Web deployment evidence

- Daily development remains local: double-clicking `start-web-dev.cmd` starts repository Vite at `http://127.0.0.1:5173/?fixture=chat`, waits for readiness and opens the fixture. The launcher was exercised through real `cmd.exe`; the full fixture loaded with 0 browser errors/warnings, and the test server was then stopped.
- Large-version synchronization is explicit: `deploy-web.cmd` runs the verified PowerShell deployment and opens `https://hklight.2000521.xyz/relaycove-web/` only after success. There is no deploy-on-save behavior.
- One-time Nginx provisioning succeeded with backup `/var/backups/relaycove-web/nginx/20260812T073422Z`. Only exact `/relaycove-web` locations were added before the existing Zulip root fallback.
- Current server release: `20260812T105302Z-1374985197ac-worktree`; archive SHA-256 `DB1A2948B2E70E3CE2F30104E44E46E18E99E9C52CA7ADE8E94E243E9CFAE8E3`. Immediate previous release `20260812T104920Z-1374985197ac-worktree` and earlier releases are retained for explicit rollback.
- The four deployed files (`index.html`, JavaScript, CSS and favicon) byte-match the final local `dist/` by SHA-256. The incoming directory is empty; Nginx and the legacy RelayCove service are active.
- Public HTTPS checks passed: `/relaycove-web` → 308; `/relaycove-web/`, hashed JavaScript, bundled favicon and a client deep link → 200; missing hashed asset → 404. HTML is `no-cache`; hashed assets are immutable; CSP/frame denial, HSTS, no-referrer and nosniff headers are present.
- After the final Slice 3 deployment, a fresh anonymous Chromium loaded the 1440×900 formal login shell with the corrected formal-client copy, 0 console errors/warnings and no horizontal overflow. The deployed `index-C1hNsbDg.js` byte-matches local `dist` (SHA-256 `ECAFA80E55EFC0F7E39BC894293D0D9CF3E098B541FA419209292385285DAFB8`); no credentials were entered.
- The existing official Zulip root still returns its prior redirect, legacy `/relaycove/` retains its prior response, and the legacy service remains active. The static path is same-origin with Zulip and adds no server/BFF/API proxy.

## Remaining capability gates / known differences

- Search, non-image attachments, reactions, mentions, membership/presence, saved messages and channel management remain separate capability gates. Visual affordances are disabled or explicitly unavailable.
- The fixture still proves visual/interaction structure only. The production login now starts the formal Zulip session path; no fixture account/message enters that graph.
- Messages, queue/cursor and outbox are page-memory only. RelayCove.Web does not yet claim refresh-offline history or Service Worker/IndexedDB caching.
- Loaded messages are currently rendered directly. Automatic scroll-threshold paging, long-list virtualization and cross-page visual-anchor preservation remain a Web performance slice; the current verified control loads explicit 50-message pages.
- The chosen same-origin static deployment and HTTPS/CSP/security headers are verified. A future move to another origin would reopen the Zulip CORS gate; no proxy/BFF may be added to bypass it.
- A user-authorized member credential was used after the logout report for two bounded checks. First, `fetch_api_key -> users/me -> register -> delete queue` returned 200 throughout. Then a fresh headless Chromium loaded the deployed UI, remained signed in, completed the second `users/me`, register, all topic reads and one initial history read with 200, opened the event long-poll, reported zero console issues and completed queue-delete/logout with 200. No send or mark-read request occurred; no message content was emitted into evidence. This verifies the corrected browser login/read boundary, but not event delivery continuity, read-state mutation or text sending. Full Live acceptance still requires dedicated accounts, an isolated channel and explicit write authorization.
- Stage 22M native MAUI visual parity has not started. Browser evidence does not prove native Windows visual behavior.

## Stage 21 external gates still unverified

- Final ZIP launch on a clean Windows 11 x64 VM without .NET or Windows App SDK Runtime.
- Live contract/write test using two dedicated test accounts and an isolated private channel.
- One manual password-login acceptance in the final MAUI UI using a dedicated account.
- Native MAUI visual acceptance, signing, installer and public release.

Do not mark Stage 21 complete until the clean-VM, Live and manual MAUI password-login evidence exists.

## Git handoff

- Current branch and HEAD remain `codex/stage-22w-web-foundation` at `1374985`.
- The Stage 22W documentation/code/test changes are intentionally unstaged and uncommitted for user review.
- The fixed static Web entrance was provisioned and deployed only after the user's explicit follow-up authorization. No commit, push or tag occurred. The later login diagnosis used one user-selected credential only for password exchange, identity/register/topic reads, one history page, event-loop startup and queue cleanup; it did not mark anything read or send a message. Ordinary Fast/Full remain external-network-free.
