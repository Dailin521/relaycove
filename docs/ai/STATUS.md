# RelayCove Status — Stage 21 / 22W / 22M

Updated: 2026-08-13
Branch: `codex/stage-22m-web-parity` (isolated worktree `E:\WorkSpace\RelayCove-Stage22MParity`)
Integration commits: Web `573a33d`; first MAUI slice `c1c4da9`; native Web-parity follow-up `259e176`
Current delivery: Stage 22W and both current Stage 22M slices are integrated. The native Web-parity follow-up passed its current-tree Full gate and independent review, was pushed on `codex/stage-22m-web-parity`, and fast-forwarded to remote `main` at `259e176`. The concurrently dirty primary worktree was intentionally left unchanged.

## Product direction

- The official Zulip Web remains unchanged.
- `RelayCove.Web` is an independently deployable formal client; `RelayCove.App` remains native .NET MAUI without WebView.
- Both frontends connect directly to the same Zulip Realm. Zulip remains the only business source of truth; there is no RelayCove server, BFF, proxy protocol or second message backend.
- Web is implemented and accepted first. A versioned interaction contract then becomes the input for Stage 22M native parity.
- The two frontends share visual tokens, interaction specifications, capability matrices and acceptance scenarios, but no UI runtime code.

## Stage 22M native Web-parity follow-up — 2026-08-13

- This pass is isolated from the concurrently dirty primary worktree on `codex/stage-22m-web-parity`, based on `46e5608`. No primary-worktree file, Web runtime, Zulip protocol, credential, deployment or frozen baseline was changed.
- The native shell now follows the formal Web surface hierarchy: product bar/navigation/conversation pane use `Ground`, chat uses `Surface`, Composer/details use `SurfaceSoft`, and 1 px borders separate the large regions. Shared token values remain unchanged; the fix is semantic token assignment rather than a second palette.
- Wide/compact/narrow breakpoints are aligned at `>=1121`, `721–1120` and `<=720`; standard conversation width is 310 DIP. The minimum Windows width permits a real 640 DIP narrow capture at 150% scaling.
- Product/navigation/chat headers, continuous collapsible channel/DM groups, 67 DIP conversation rows, own/other messages, structured quotes, attachment cards, multiline Composer, details and settings were rebuilt with bundled native SVG resources. No WebView or runtime UI code is shared with React.
- Pointer/focus quick actions now mirror Web capability choice: other messages expose reaction/star/quote/copy/more, own messages expose edit/star/quote/copy/more. Right-click/`Shift+F10` opens the complete menu next to its trigger and flips/clamps at viewport edges; the message menu and account panel are non-dimming popovers with Escape/Tab/arrow-key exits and focus restoration.
- Composer selection and Windows file drop share one validation path for up to ten image or non-image attachments. Native previews remain limited to safe image types; all eventual network/write behavior continues to flow only through `IClientSession`.
- Appearance settings now use the Web 218 DIP sidebar on standard/wide layouts and a horizontally scrollable top category bar at `<=720` DIP, with an up-to-820 DIP card, device-only theme/font/conversation-width/default-details preferences and real light/dark switching. Unsupported server capabilities remain explicitly unavailable rather than being visually fabricated.
- Deterministic Debug screenshots used only `RELAYCOVE_NATIVE_UI_PREVIEW=1`; that session is in-memory and cannot contact or write to a Realm.
- The current Visual Studio 2026 baseline is now explicit: `global.json` selects SDK `10.0.400` with `latestPatch`; the installed workload set is `10.0.400.1`; `maui-windows` is `10.0.20/10.0.100`; `RelayCove.App` pins MAUI `10.0.20` and Windows `win-x64`. The upgraded SDK/MAUI/RID graph was explicitly provisioned once; ordinary Fast/Full remain no-restore.
- On this UI-parity branch, `RelayCove.App/Properties/launchSettings.json` keeps the MAUI-aware `Windows Machine` profile and only adds `RELAYCOVE_NATIVE_UI_PREVIEW=1`. This preserves Visual Studio's TFM/RID launch handling; the previously attempted fixed-executable profile was rejected because it looked for a nonexistent no-RID output.
- XAML saved in Visual Studio can use XAML Hot Reload/Live Visual Tree. External Codex file edits are not assumed to hot reload, and current `dotnet watch --list` does not include raw XAML. The stable agent loop is a coherent batch, App-only Debug `--no-restore` build, then one preview restart; screenshots and Fast remain component/Slice checkpoints.
- The Windows adapter now places only the Debug offline preview on a non-primary display, falls back safely when none exists and converts the target 1440×900 DIP using that monitor's scale. The placement does not run in production and does not change the Realm/session boundary. Narrow evidence passed App 45/45 before the delivery gate; no Live or real Realm request ran.
- Final current-tree `pwsh ./scripts/verify.ps1 -Mode Full` passed after explicit one-time SDK/MAUI/Release-RID provisioning: Debug and Release builds had zero warnings/errors; Core 85/85, Zulip.Client 40/40, Data 17/17 and App 45/45 passed in each configuration (187/187 each); Web deployment-template checks, typecheck, 86/86 unit tests and production build passed; Playwright passed 6/6 plus fixed deployment path 1/1. The final Windows package SHA-256 is `65900B913C0CD5AAC169ED6383BAF0DCD2A0441520F38C2466415BABE0D7B505`. Full did not run Live, use credentials or contact a Realm.

Current native evidence on `DISPLAY2` at 150% / DPI 144:

| Evidence | Target DIP | Actual pixels | SHA-256 |
|---|---:|---:|---|
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-1440-light-final.png` | 1440×900 | 2160×1350 | `149BF35FC4BE4ED6EFD0EE7DBB0A2D37F59A03103FEE0769B5BEB943C3EDD604` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-1440-dark-final.png` | 1440×900 | 2160×1350 | `BECF517F4D634ED349B8D84092265C99C4E5A7B8CF1B507F9DAAA74399382C78` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-1024-light-final.png` | 1024×768 | 1536×1152 | `4AE8EFDA4C7ACD2838D538A1DAA5D091584E0537BB544186B862074757A1CD5A` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-640-chat-light-final.png` | 640×900 | 960×1350 | `248A717655BD95D875AC3B0609EE7B8BC1DFA1B6228EE761FE68A2A3D7D31E7F` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-640-list-light-final.png` | 640×900 | 960×1350 | `53CFE90E20E2659C1E54CC0F32F049F303BFDE05583F767AF952201B6B6FA291` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-shell-hover-actions-final.png` | 1440×900 | 2160×1350 | `818DF5693F6C321D21F891675BC930BA3D4D7B78FCE6B1BC3F7D1380A6FA5FF5` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-message-menu-1440-light-final.png` | 1440×900 | 2160×1350 | `32B2401EA467E53631DB3E83E6A7DB97B91DFE3A28E704C964E3ECEAF957CF18` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-details-1440-light-final.png` | 1440×900 | 2160×1350 | `F70DA7E235410FA84D8A023D3AF1822D9A8151CD4AFD7452B90EA6F1B061CACD` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-settings-1440-light-final.png` | 1440×900 | 2160×1350 | `A79F7F73C817BAE26444C8153FEA748219302823F23ABC6460F8DF5E0B50CEF7` |
| `artifacts/maui/screenshots/stage22m-web-parity/maui-settings-640-light-final.png` | 640×900 | 960×1350 | `3D57B1C4F27AB70ED2CAFE15A566E7AD1247F3615E8C64A94BF0683D338358B2` |

These captures prove deterministic native composition and responsive/theme behavior, not Live Zulip behavior. Stage 21 Live, native manual password login, real-Realm message/file writes, 100%/200%, high-contrast, package/install and clean-VM gates remain unverified.

## Stage 22M native shell and message interactions — 2026-08-13

### Isolation and implementation scope

- Work is isolated under `E:\WorkSpace\RelayCove-Stage22M` on `codex/stage-22m-native-shell`, based exactly on `main@53a4f1a643031d9eef801e52ab8b20456ea8773c`. The concurrently modified primary worktree was inspected read-only and left untouched.
- Changes are confined to the native `RelayCove.App` path, shared `RelayCove.Core` contracts/session, `RelayCove.Zulip.Client`, `RelayCove.Data`, deterministic .NET tests and Stage 22M documentation. `src/RelayCove.Web`, deployment configuration and every frozen `docs/ui/baselines/chat-ui-v1/` file remain unchanged.
- The shell uses native .NET MAUI XAML, Views, ViewModels, Windows behaviors and a Windows window adapter. It contains no WebView, React runtime, RelayCove server, BFF, proxy or second message backend.
- Native ResourceDictionaries define shared color, brush, spacing, radius, typography and shell-size tokens for light/dark themes. The window uses the MAUI native `TitleBar`, a 1440×900 default, a 720×560 minimum and the Windows adapter for always-on-top state.
- Component boundaries now cover the product bar, primary navigation, channel/topic/DM pane, chat header, virtualized `CollectionView` messages, multiline Composer, collapsible details, contacts and settings. Message presentation includes own/other alignment, date/unread dividers, quotes, avatars, reactions, image/file cards and outbox/mutation state.
- Responsive projection has wide (`>=1200`), compact (`820–1199`) and narrow (`<=819`) modes. At 1024 DIP, details default closed and reopen as an overlay; narrow mode switches between the conversation list and chat.
- Composer input is 72–300 DIP, preserves a draft per canonical conversation, uses `Ctrl+Enter` for send and normal Enter for newline, and preserves newer input or another conversation's draft while an earlier send is pending. Details `Escape` closes the overlay, Tab is contained in the modal scope and focus returns to the trigger.

### `IClientSession` wiring and interaction boundary

- Production DI resolves the same `ClientSession` boundary over `RelayCove.Core`, `RelayCove.Zulip.Client` and `RelayCove.Data`. `ShellViewModel` subscribes to `IClientSession.StateChanged` and projects subscriptions, topics, recent DMs, users, messages, authoritative unread, connection, outbox and per-message mutation state; Views do not call HTTP or SQLite.
- Restore, manual password login, selection, topic/older-history loading, server-confirmed mark-read, logout and per-account cache clearing remain intact. Shared contracts now also cover reaction, edit with `prev_content_sha256`, permanent delete, star/unstar, attachment upload and controlled same-Realm media reads.
- Each message has one mutation lane. Submitting or uncertain operations block later mutations for that message; ambiguous network/protocol outcomes become `Uncertain`, switch offline where appropriate and are never automatically retried. Upload and message send remain two separate non-idempotent stages; a confirmed upload reference can be reused only by an explicit user retry.
- The native shell implements right-click/`Shift+F10`/touch message menus, quote/copy/permalink/ID actions, 24-emoji Composer/reaction pickers, edit/delete confirmation, local loaded-workspace search, new 1:1/group/self-DM selection, image preview/download and per-conversation attachment drafts. All server writes still flow only through `IClientSession`.
- Contacts and new-DM candidates are explicitly the active users currently known to the session, not a complete member directory. Member counts/relationships, presence, common channels, the saved-message list, server-wide search/mentions and channel management remain hidden or labelled unavailable; users are never reinterpreted as channel membership.
- A `#if DEBUG` `NativeShellPreviewSession` is selected only when `RELAYCOVE_NATIVE_UI_PREVIEW=1`. It has deterministic in-memory `IClientSession` state, no gateway/HTTP dependency, and applies send/reaction/edit/delete/star/upload interactions only to local memory for visual testing. It cannot write to a Realm. Release and ordinary Debug startup continue to use the production session.

### Current validation evidence

- One explicit `dotnet restore RelayCove.sln --nologo` provisioned this new worktree. It did not use a target Realm or credential.
- Narrow `RelayCove.App.Tests`: 36/36 passed.
- Final `pwsh ./scripts/verify.ps1 -Mode Fast`: passed with zero build warnings/errors; Debug tests Core 85/85, Zulip.Client 40/40, Data 17/17 and App 36/36 (178/178 total); Web deployment-template test, typecheck, 63/63 unit tests and production build also passed. Fast did not run `RelayCove.Zulip.LiveTests`.
- Final `pwsh ./scripts/verify.ps1 -Mode Full`: passed after fixing all 13 Release XamlC source-type failures without disabling compiled bindings; Debug/Release .NET tests passed 178/178, Web passed 63/63, Playwright passed 6/6 plus deployment path 1/1, and the Windows package was generated with zero build warnings/errors. `Live` did not run.
- This new worktree was explicitly provisioned with `dotnet restore RelayCove.sln` and `npm ci` before the final no-restore Fast run. The resulting Web `node_modules`/`dist` are ignored; no tracked Web source changed.

### Native Windows screenshot evidence

These captures are Debug preview evidence from 2026-08-13 on `DISPLAY2`, Windows scaling 150% (`DPI=144`). `PrintWindow(PW_RENDERFULLCONTENT)` rendered only the RelayCove top-level window, including the MAUI title bar; neither the desktop, the primary display nor windows behind RelayCove entered the PNGs. DWM extended-frame dimensions were iterated to the target DIP size before capture.

| Evidence | Target/equivalent DIP | PNG physical pixels | SHA-256 |
|---|---:|---:|---|
| `artifacts/maui/screenshots/native-shell-wide-display2-150pct-light-stage22m-parity.png` | 1440×900 | 2160×1350 | `B8A38A4C8A93B7234D1557A10EAC062C2ADACD9D9D2D766021F8613B4DDDF104` |
| `artifacts/maui/screenshots/native-shell-compact-display2-150pct-light-stage22m-parity.png` | 1024×768 | 1536×1152 | `7F194E2917C2788CCD42289720AC09D2B9DC1F8F0AC0A8D104FB4330E6A72E8C` |
| `artifacts/maui/screenshots/native-shell-compact-display2-150pct-dark-stage22m-parity.png` | 1024×768 | 1536×1152 | `CD60F951E394D569AFB73CA1870240B257C283DFDF95C7F8A472AC617EF95F12` |
| `artifacts/maui/screenshots/native-message-menu-compact-display2-150pct-dark-stage22m-parity.png` | 1024×768 | 1536×1152 | `6C8A2FB72E4DED1CD86CD26FF91EF1EB54CC034DDD1316F779DCB15A705B3E3C` |

The final Debug binary started with the no-network preview session and rendered native title-bar/window controls, navigation, channel/topic/DM projection, own/other messages, quote/reaction/image presentation, Composer and details at both target sizes without visible horizontal clipping at 1024×768. Real pointer and Windows UI Automation checks opened the full message menu, placed initial focus on its first action, closed it back to the trigger, navigated settings and switched light/dark. They do not verify a real Realm or write, 100%/200% scaling, full manual keyboard traversal, high contrast, long-list anchors, signing, installation or a clean VM.

### Independent review

- Independent read-only tracks covered native UI/accessibility, `IClientSession`/draft/send concurrency, protocol/data/media safety and worktree/frozen-baseline/evidence boundaries.
- One P1 found that an online A→B channel browse could be reverted by an intermediate state publication for old conversation A. The UI browsing channel is now preserved while it remains subscribed; revocation can still clear it. A deterministic state-publication regression test passes.
- One P1 found that compact details did not enter or contain keyboard focus. The overlay now focuses its close button, disables the background shell, contains Tab/Escape and restores focus to the details trigger. The original reviewer rechecked the fix and found no remaining P0/P1; real keyboard timing remains a manual acceptance item.
- One evidence P1 found misleading viewport-style screenshot names and missing DPI/physical-size records. The final evidence uses wide/compact display-and-scale names and records target DIP, actual PNG pixels, capture scope, DPI, hashes and limitations above.
- Final review found one documentation P1: the task/status records still described the old shell-only state and fail-closed preview writes. The records now match the in-memory-only interaction preview, current test counts and current screenshots. Architecture/security and final UI re-review reported no remaining confirmed P0/P1.

## Stage 22W foundation, formal message client and first complete interaction capability

- Starting state was clean `main` at `1374985`; all four `docs/ui/baselines/chat-ui-v1/` SHA-256 values were recomputed and matched before work.
- Local toolchain: Node `v24.11.1`, npm `11.10.0`, repository Playwright `1.62.1`; the locked Web stack is TypeScript 7, React 19 and Vite 8.
- `src/RelayCove.Web/` contains separate application/auth routing, product/window bar, primary navigation, collapsible channel/DM/topic/contact list, chat header/message list, multiline Composer, collapsible details and settings components. Visual tokens are isolated under `src/styles/tokens.css`.
- Light/dark, 1440×900, 1024×768 and below-720 single-pane layouts are implemented. Composer pointer/keyboard sizing is clamped to 72–300 px; text and attachment drafts are keyed by conversation, while channel/DM group state is a non-sensitive local preference.
- Deterministic visual data is confined to `src/fixtures/`. Vite selects the fixture-aware entry only for explicit `fixture`/`e2e` modes; ordinary development and production both use the formal App, while the production graph/bundle exclude fixture accounts, labels and chat payloads.
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

## 2026-08-13 local message-interaction follow-up

- Own realtime/history messages are mapped read even if a custom Zulip client echo omits the `read` flag, so sending cannot create a local unread badge. Other senders remain server-flag authoritative.
- Every message exposes Quote, Copy and More controls on hover/focus and persistently on touch layouts; the existing right-click/keyboard menu remains the complete fallback.
- Quote drafts now follow the Zulip 12.1 fenced-quote shape and use complete raw Markdown plus the sender/permalink, preserving text, image links and non-image attachment links.
- The Composer smile button opens a 24-item Unicode emoji picker, supports keyboard/Escape/outside-close behavior, inserts at the current selection and restores textarea focus.
- Reaction add/remove, own-message edit, own-message permanent delete and per-account star add/remove now use the official Zulip endpoints and full event/history projection. Per-message writes serialize; ambiguous network/timeout results are never retried automatically.
- Daily Vite and `start-web-dev.cmd` now open `/` and the formal real-Realm login. Fixture runtime is available only through explicit fixture/E2E mode and remains excluded from production.
- A stable self-DM entry is projected from the authenticated user ID even when the server has no recent self-DM history, so local real-write verification does not depend on fixture or stale recent-conversation data.
- Channel and direct-message groups are independently collapsible, keyboard reachable and persisted locally; active search temporarily reveals matching rows without destroying the saved collapse choice.
- Composer accepts up to 10 arbitrary files by multi-select or file drag/drop. Safe raster images receive local previews; SVG/HTML/PDF/Office/archive and other files remain non-inline cards. Files upload sequentially through the existing authenticated boundary and one final message contains every escaped server-returned Markdown link.
- Same-Realm non-image upload links project as download cards. A click first obtains a temporary URL through authenticated Zulip API, then downloads a bounded Blob without placing Basic credentials or API keys in the final request, URL or DOM.
- Channel details expose confirmed current-user unsubscribe through `DELETE /users/me/subscriptions` using the exact subscription name. Success and already-unsubscribed responses reuse the existing authoritative `subscriptionRemoved` reducer cleanup; unknown results are never automatically retried. Real channels were not modified during verification.
- Narrow verification per the quick-change request: typecheck passed; 86/86 unit tests passed; production build passed at 331.92 kB JS and 36.89 kB CSS before gzip. Playwright passed 6/6 fixture/formal scenarios plus 1/1 fixed deployment-path scenario, including collapse, arbitrary/multiple attachment and unsubscribe coverage.
- A local headless Chromium logged into the target Realm, displayed real conversations/messages and completed the final post-review rerun with same-Realm public avatars visible and zero console errors. Public avatars load without credentials/referrer; protected cross-origin fallback stays unavailable rather than weakening the Realm boundary.
- With the user's explicit write authorization, one temporary self-DM completed send → reaction add → star → edit → reaction remove → unstar → permanent delete. Additional emoji-name and star/edit-event probes were deleted; the latter confirmed `update_message flags=starred` and the server message remained starred. No other user's message, channel, upload or mark-read was touched.
- Independent protocol/security and UI/state reviews found no remaining P0/P1 after the public-avatar origin boundary was tightened and regression-tested.
- The post-review repository `Full` gate passed on this exact Web follow-up tree: .NET Debug/Release 135/135, Web 86/86, Playwright 6/6 plus fixed deployment path 1/1, Windows package generation, and zero build warnings/errors. No commit, push or deployment had been performed when this evidence was recorded.

## Current repository verification evidence

- Final combined-main `pwsh ./scripts/verify.ps1 -Mode Full` passed on 2026-08-13 after integrating both owning commits. Debug and Release each passed Core 85/85, Zulip.Client 40/40, Data 17/17 and App 36/36 (178/178 per configuration) with zero build warnings/errors.
- The same Full run passed Web typecheck, 86/86 unit tests, production build, Playwright 6/6 fixture/formal scenarios and 1/1 production deployment-path scenario.
- Fast/Full require explicitly pre-provisioned NuGet/npm/Chromium assets and use no `.NET restore`, package install, real credential or target-Realm request. Playwright serves only `127.0.0.1` and intercepts the fake Zulip origin.
- Playwright covered mocked login/restore/logout, console errors and warnings, keyboard focus, Composer clamp, details Escape, 640 px list/chat switching and both list/chat horizontal-overflow checks.
- Windows package: `artifacts/package/RelayCove-2.0.0-alpha.1-win-x64.zip`, SHA-256 `8EE2375AC79A181B4E1106A3B68D905D7D7E083C6653500F083B117DB2E0734C`. It was generated locally and not published.

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

- Daily development remains local: double-clicking `start-web-dev.cmd` starts repository Vite at `http://127.0.0.1:5173/`, waits for readiness and opens the formal login/real Realm client. Fixture pages are automation-only and must be enabled explicitly.
- Large-version synchronization is explicit: `deploy-web.cmd` runs the verified PowerShell deployment and opens `https://hklight.2000521.xyz/relaycove-web/` only after success. There is no deploy-on-save behavior.
- One-time Nginx provisioning succeeded with backup `/var/backups/relaycove-web/nginx/20260812T073422Z`. Only exact `/relaycove-web` locations were added before the existing Zulip root fallback.
- Current server release: `20260812T105302Z-1374985197ac-worktree`; archive SHA-256 `DB1A2948B2E70E3CE2F30104E44E46E18E99E9C52CA7ADE8E94E243E9CFAE8E3`. Immediate previous release `20260812T104920Z-1374985197ac-worktree` and earlier releases are retained for explicit rollback.
- The four deployed files (`index.html`, JavaScript, CSS and favicon) byte-match the final local `dist/` by SHA-256. The incoming directory is empty; Nginx and the legacy RelayCove service are active.
- Public HTTPS checks passed: `/relaycove-web` → 308; `/relaycove-web/`, hashed JavaScript, bundled favicon and a client deep link → 200; missing hashed asset → 404. HTML is `no-cache`; hashed assets are immutable; CSP/frame denial, HSTS, no-referrer and nosniff headers are present.
- After the final Slice 3 deployment, a fresh anonymous Chromium loaded the 1440×900 formal login shell with the corrected formal-client copy, 0 console errors/warnings and no horizontal overflow. The deployed `index-C1hNsbDg.js` byte-matches local `dist` (SHA-256 `ECAFA80E55EFC0F7E39BC894293D0D9CF3E098B541FA419209292385285DAFB8`); no credentials were entered.
- The existing official Zulip root still returns its prior redirect, legacy `/relaycove/` retains its prior response, and the legacy service remains active. The static path is same-origin with Zulip and adds no server/BFF/API proxy.

## Remaining capability gates / known differences

- Web implements arbitrary attachment cards/upload, current-user unsubscribe, reaction, own-message edit/delete and per-account starred state; its bounded self-DM mutation probe was deleted after verification. MAUI implements loaded-state local search, image/file attachment presentation/upload contracts, reactions, edit/delete and star/unstar UI/session paths, but these native writes have deterministic fake/in-memory coverage only. Server-wide search/mentions, complete membership/presence/common-channel data, saved-message list, channel subscribe/create/rename/archive/member management and resumable large-file upload remain separate gates.
- The fixture still proves visual/interaction structure only. The production login now starts the formal Zulip session path; no fixture account/message enters that graph.
- Messages, queue/cursor and outbox are page-memory only. RelayCove.Web does not yet claim refresh-offline history or Service Worker/IndexedDB caching.
- Loaded messages are currently rendered directly. Automatic scroll-threshold paging, long-list virtualization and cross-page visual-anchor preservation remain a Web performance slice; the current verified control loads explicit 50-message pages.
- The chosen same-origin static deployment and HTTPS/CSP/security headers are verified. A future move to another origin would reopen the Zulip CORS gate; no proxy/BFF may be added to bypass it.
- A user-authorized member credential first completed bounded Web login/read checks. On 2026-08-13 the user separately authorized the temporary self-DM mutation sequence documented above; it passed and all probe messages were deleted. This verifies the implemented Web endpoint set against the target Realm, but not the Stage 21 two-account isolated-channel Live gate, real image upload, channel unsubscribe or mark-read.
- Stage 22M native shell and message-interaction parity are implemented and have bounded real-window light/dark/menu/focus evidence. Full external acceptance remains open: browser evidence does not prove native behavior, and native local/fake protocol coverage does not prove real Realm writes.

## Stage 21 external gates still unverified

- Final ZIP launch on a clean Windows 11 x64 VM without .NET or Windows App SDK Runtime.
- Live contract/write test using two dedicated test accounts and an isolated private channel.
- One manual password-login acceptance in the final MAUI UI using a dedicated account.
- Remaining native MAUI acceptance at 100%/200%, full manual keyboard/high-contrast/long-list scenarios, plus signing, installer and public release.

Do not mark Stage 21 complete until the clean-VM, Live and manual MAUI password-login evidence exists.

## Git handoff

- Web commit `573a33d` and MAUI commit `c1c4da9` preserve the two implementations as independently reviewable history and are integrated into `main` under the user's explicit commit/merge/push authorization.
- The fixed static Web entrance remains on the previously recorded Slice 3 server release; this integration does not deploy a new Web build or alter the Zulip host.
- Stage 22M made no target-Realm request, real mark-read or message write. The Web credential was used only for the documented bounded read and temporary self-DM checks; all temporary messages were deleted and no secret was printed or persisted by the harness.
