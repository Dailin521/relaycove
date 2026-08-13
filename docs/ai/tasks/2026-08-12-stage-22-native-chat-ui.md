# Stage 22 — RelayCove 双前端（22W Web / 22M Native MAUI）

- Status: Stage 22W message-interaction follow-up and Stage 22M native shell/message-interaction parity implemented, independently reviewed and integrated into `main`; final combined Full passed before the authorized push
- Integration branch: `main`; owning branches `codex/stage-22w-message-interactions` and `codex/stage-22m-native-shell`
- Starting point: both owning branches began from `main@53a4f1a643031d9eef801e52ab8b20456ea8773c`
- Design source: immutable `docs/ui/baselines/chat-ui-v1/`
- Interaction source: `docs/ui/INTERACTION_SPEC.md`

## Confirmed product decision

1. 现有 Zulip 官方 Web 保留，不修改、不替换。
2. `RelayCove.Web` 是可以独立部署和正式使用的 Web 客户端。
3. `RelayCove.App` 继续使用 .NET MAUI，以原生 XAML/ViewModel 复刻冻结后的 Web 交互，不使用 WebView。
4. Web 与 MAUI 都直接连接同一个 Zulip Realm；Zulip 是唯一业务事实源。
5. 不新增 RelayCove server、BFF、代理协议或第二套消息后端。
6. 两端共享视觉 Token、交互规格、功能矩阵和验收场景，但不共享 UI 运行时代码。
7. Web 默认“记住登录”，允许把 API Key 保存在当前浏览器 local storage；注销清除 local/session storage。

Stage 21 的真实 Realm Live、MAUI 人工密码登录和干净 Windows 11 x64 VM 门禁仍未完成。本任务不能被用来把 Stage 21 标记为完成。

## Stage split

### Stage 22W — RelayCove Web

先实现、测试和验收正式 Web。每个交互版本通过浏览器门禁后记录截图、Token、功能矩阵、验收场景与已知差异，再作为 Stage 22M 输入。

### Stage 22M — MAUI native parity

状态：Slice 1 and message-interaction parity implemented in isolated worktree。使用原生 XAML、ResourceDictionary、ViewModel、Behavior 和 Windows adapter，不引用 React runtime，不加载 WebView。当前实现包含可运行原生壳、既有 session 投影、reaction/edit/delete/star/附件写能力以及确定性 App/Core/Zulip/Data 测试；真实 Realm 写入和完整 Windows 人工验收仍是独立未验证门禁。

## Stage 22W / Slice 1 — production foundation

### Production project

- `src/RelayCove.Web/`：TypeScript 7、React 19、Vite 8。
- 本地 bundle：React、Lucide 图标与全部运行依赖；生产 HTML 无运行时 CDN。
- `package-lock.json` 固定依赖；NuGet、npm 和 Chromium 由单独 bootstrap 显式预置，普通 Fast/Full 不恢复/安装依赖或下载浏览器。
- 生产构建排除集中在 `src/fixtures/` 的演示数据；E2E 专用构建才启用 `?fixture=chat`。

### Component boundaries

| Responsibility | Landing point |
|---|---|
| application/auth routing | `src/RelayCove.Web/src/App.tsx` |
| product bar/window shell | `src/RelayCove.Web/src/components/ProductBar.tsx` |
| primary navigation | `src/RelayCove.Web/src/components/NavigationRail.tsx` |
| channel/DM groups | `src/RelayCove.Web/src/components/ConversationPane.tsx` |
| chat header/messages | `src/RelayCove.Web/src/components/ChatHeader.tsx`, `MessageList.tsx` |
| message actions/media | `src/RelayCove.Web/src/components/MessageContextMenu.tsx`, `MessageImage.tsx`, `RealmMedia.tsx` |
| multiline composer | `src/RelayCove.Web/src/components/Composer.tsx` |
| collapsible details | `src/RelayCove.Web/src/components/DetailsPane.tsx` |
| settings/account | `src/RelayCove.Web/src/components/SettingsPage.tsx` |
| shared visual tokens | `src/RelayCove.Web/src/styles/tokens.css` |
| deterministic demo state | `src/RelayCove.Web/src/fixtures/` |

Implemented shell behavior in this Slice:

- top product bar and browser-safe window-control visual states;
- primary navigation and explicit unavailable capability states;
- channel/direct-message grouping and deterministic fixture filtering;
- chat header, raw-text message skeleton, day/unread separators;
- per-conversation draft state and 72–300 px multiline Composer sizing;
- collapsible details with no inferred membership/presence/capability;
- settings skeleton, light/dark themes and 1440×900 / 1024×768 / narrow layout;
- below 720 px, conversation-list/chat single-pane navigation.

The fixture is visual evidence only. It does not represent a Zulip register snapshot, does not enter production builds, and never shares the formal API/session path.

### Zulip browser API and credential boundary

- Realm validation accepts only a canonical HTTPS origin without userinfo, path, query or fragment.
- `GET /api/v1/server_settings` is sent without credentials and checks feature level 500, compatibility and email authentication.
- `POST /api/v1/fetch_api_key` sends username/password as form data; password is not persisted.
- `createAuthenticatedRequest` applies HTTP Basic `email:apiKey` while keeping the key out of the URL.
- fetch uses `redirect: error`, `credentials: omit`, `cache: no-store` and `no-referrer`.
- remember-login defaults to true: local storage when checked, session storage when unchecked.
- logout clears both browser stores; corrupted credential JSON fails closed.
- errors expose only fixed categories/status, not request body, password, API key or server body.

All current login/API tests use fake HTTP. No real account or credential is required for this Slice. The selected same-origin `/relaycove-web/` static deployment has passed its browser/HTTPS/security-header check; moving to another origin would reopen the CORS gate, which must not be bypassed with a new proxy.

## Stage 22W / Slice 2 — formal Zulip message client

Implemented production path:

- login completes `server_settings -> fetch_api_key -> users/me`, verifies the returned user ID before persisting a complete browser session;
- `POST /register` publishes subscriptions, users, recent DMs, unread and limits as one reducer snapshot; queue ID/cursor remain private in-memory session fields;
- `GET /events` long-polls with the server timeout, handles heartbeat/unknown events, message/edit/delete/move/flags, subscription/stream/user/restart, 401, 429, network backoff and `BAD_EVENT_QUEUE_ID` re-registration;
- subscribed channel topics, known active contacts, 1:1/group/self-DM and new channel-topic entry use canonical conversation keys; no display title is parsed back into Zulip identity;
- newest/older history uses exact channel/topic or DM narrow, raw Markdown and 50-message paging; stale selection responses are aborted and rejected by lifecycle/selection epochs;
- mark-read changes the projection only after the server confirms the current narrow;
- text sends are serialized, carry queue/local identity, never auto-resend, reconcile through realtime local echo or one read-only message lookup, and expose Hidden/Waiting/WaitExpired/Failed recovery states;
- logout aborts event/history/send work, best-effort deletes the queue and then clears both credential stores; 401 fails closed to the login page;
- message projection, queue/cursor and outbox are page-memory only. Only confirmed credentials and non-sensitive appearance preferences persist in browser storage.

Production `App.tsx` imports this session/store/projector path and never imports `src/fixtures` or `src/test`. The local fixture still reuses the same visual components through the development/E2E-only Vite entry.

## Stage 22W / Slice 3 — complete message affordances and image media

Implemented production path:

- right-click, keyboard menu key/`Shift+F10` and mobile “more” expose one accessible message menu; its current actions only mutate local UI/clipboard or open the official Zulip permalink and never issue a Realm write;
- users, conversations and messages map Zulip `avatar_url`, fallback `/avatar/{user_id}` and bot identity through a same-Realm Blob loader with deterministic initials/Bot fallback;
- same-Realm PNG/JPEG/WebP/GIF/AVIF upload Markdown becomes a controlled thumbnail, Blob download and focus-trapped viewer; unsafe, SVG, cross-Realm and overflow links remain literal raw Markdown;
- Composer validates type/zero length/server/product size, keeps per-conversation text/image drafts, supports remove without losing text, uploads once and then submits the returned Markdown once;
- upload/message POST never auto-retry; a confirmed upload is reused only after an explicit subsequent send. Confirmed logout aborts upload, invalidates its epoch and releases draft URLs before clearing the session;
- resource limits are four previews per message, four concurrent Realm media reads and 64 MiB loaded Blob budget.

Formal automation covers avatar/media Basic boundaries, temporary upload URLs without Authorization on the final Blob request, image MIME/size rejection, context-menu keyboard/focus/clipboard, viewer close/download/focus, exact multipart upload + Markdown send, logout/late-result cancellation and production fixture exclusion. No real Realm upload or message write is used.

## Stage 22W — 2026-08-13 message-interaction write follow-up

- Fixed own-message unread projection at the Zulip mapper boundary; current-user messages are read even when a custom-client echo omits the flag.
- Added per-message Quote, Copy and More quick controls while retaining right-click, `Shift+F10` and touch behavior.
- Replaced the lossy image-placeholder reply with a Zulip-style fenced quote sourced from complete raw Markdown, including text, images and other attachment links.
- Added a keyboard-operable 24-item Unicode Composer emoji picker with caret insertion and focus restoration.
- Implemented official Zulip reaction POST/DELETE, own-message PATCH edit with `prev_content_sha256`, own-message permanent DELETE and per-account starred flag add/remove. Reaction/update/delete/flag events and history are mapped through the shared reducer; one message has one serialized mutation lane and ambiguous results are never automatically retried.
- Ordinary Vite and `start-web-dev.cmd` now open the formal `/` login and real Realm data; deterministic fixture state remains available only through explicit fixture/E2E mode and is excluded from production.
- Self-DM is now a deterministic production navigation entry derived only from the authenticated current user ID; it remains available even with no recent self-DM history.
- Channel and DM groups collapse independently and persist as non-sensitive browser preferences. Matching search results temporarily reveal their group.
- Composer now accepts arbitrary files by multi-select or drag/drop, keeps up to 10 per-conversation attachment drafts, previews only safe raster images, uploads in order and sends one message containing escaped server-returned links. Non-image same-Realm uploads render as file cards and download through a temporary URL/Blob boundary rather than inline active content.
- Channel details now allow the current user to unsubscribe through `DELETE /users/me/subscriptions`; success reuses the existing subscription-removal reducer cleanup, while an unknown result remains explicit and is never auto-retried.
- Verification intentionally stayed narrow: typecheck, 86/86 unit tests and production build passed; Playwright passed 6/6 fixture/formal scenarios plus 1/1 fixed deployment-path scenario. A local Chromium loaded real conversations/messages and same-Realm public avatars with zero console errors.
- With explicit user authorization, temporary self-DM message/reaction/star/edit/delete and focused flag-event probes passed against the target Realm and were deleted. No other user's message, channel, upload or mark-read was touched; channel unsubscribe and file upload remained fake-HTTP only.
- Independent security, state/test and UI reviews confirmed no remaining P0/P1 after the public-avatar origin constraint and stale quote-action Playwright assertions were corrected.
- The post-review repository `Full` gate passed on the exact follow-up tree: .NET Debug/Release 135/135, Web 86/86, Playwright 6/6 plus fixed deployment path 1/1, Windows package generation, and zero build warnings/errors. Commit, push and deployment had not been performed when this evidence was recorded.

## Remaining independent capability gates

- global/server search, mentions, complete membership/common-channel data, saved-message list, presence, channel subscribe/create/rename/archive/member management, and resumable large-file upload;
- real-Realm acceptance for native image/file upload, reaction, edit, delete, star and message-send writes;
- formal Web service worker or offline cache;
- automatic scroll-threshold paging, long-list virtualization and cross-page visual-anchor preservation;
- Stage 22M native 100%/200%, complete keyboard/high-contrast/long-list, manual password login, package/install and clean-VM acceptance.

Visual buttons for capabilities without a real contract stay hidden, disabled or explicitly marked unavailable. The bounded Web self-DM write proves its implemented mutation endpoints on the target Realm but does not satisfy Stage 21 Live, two-account event delivery, mark-read, upload or channel-unsubscribe acceptance. Fake HTTP and the no-network native preview do not prove MAUI real-Realm writes.

## Validation matrix

### Web offline Fast

```powershell
cd src/RelayCove.Web
npm run typecheck
npm run test:unit
npm run build
```

Expected: TypeScript passes; login, API, mapping, reducer, lifecycle, unread, queue rebuild and ambiguous-send unit tests pass; production bundle exists, contains no runtime CDN and excludes fixture content.

### Web browser Full

```powershell
cd src/RelayCove.Web
npm run test:e2e
```

Expected on local production preview with mock/fake HTTP:

- console errors/warnings: zero;
- 1440×900 light and dark screenshots;
- 1024×768 light screenshot and no horizontal overflow;
- keyboard focus from search to conversation;
- Composer Arrow/Home/End clamp;
- details Escape dismissal;
- below-720 list/chat switching;
- default remember-login, refresh restore and confirmed logout clearing both stores;
- password/API key absent from URL and password absent from stored credential.
- formal fake-HTTP journey: users/me, register, topic/DM history, server-confirmed read, one text send, read-only reconciliation, refresh restore and queue cleanup;
- message actions, protected avatar fallback/success, controlled image viewer/download, image draft remove, one upload + one Markdown send and logout-during-upload cancellation;
- group-DM participant identity and production components remain stable without fixture imports.

The production-path deployment preview separately checks `/relaycove-web/` asset URLs, favicon, security/cache headers, strict missing-asset 404, same-prefix SPA fallback and zero console errors.

### Repository gates

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
```

Fast runs the existing .NET gate with already-provisioned NuGet assets and `--no-restore`, plus Web typecheck/unit/production build. Full preserves the MAUI Release/package gate and adds Web Playwright. Neither mode restores/installs packages, downloads browsers, uses a real credential, accesses the target Realm or otherwise requires external network.

## Evidence paths

- `artifacts/web/screenshots/desktop-1440-light.png`
- `artifacts/web/screenshots/desktop-1440-dark.png`
- `artifacts/web/screenshots/desktop-1024-light.png`
- `artifacts/web/screenshots/formal-client-fake-1440-light.png`
- `artifacts/web/screenshots/message-actions-1024-light.png`
- `artifacts/web/screenshots/image-preview-1024-light.png`
- `artifacts/web/screenshots/composer-image-1440-light.png`
- `artifacts/web/playwright/report/`
- `output/playwright/relaycove-web-formal-login-1440x900.png`

Artifacts are intentionally ignored by Git; their dimensions and results are recorded in STATUS at handoff.

## Slice 1 completion evidence — 2026-08-12

- Final `pwsh ./scripts/verify.ps1 -Mode Fast`: passed with zero build warnings/errors, .NET Debug 135/135, deployment-tool regression, Web typecheck, Web unit 22/22, production build and expanded subpath/favicon/fixture/CDN output scan.
- Final `pwsh ./scripts/verify.ps1 -Mode Full`: passed; Fast repeated, .NET Release 135/135, Windows app package produced locally, fixture Playwright 5/5 and deployment-path Playwright 1/1.
- Production Web output: `224.60 kB` JavaScript and `20.63 kB` CSS before gzip; fixture account, label and business payload markers absent.
- Windows package SHA-256: `2067E791B1AB6FA678EBB2D4C2FC7939C2EA391017A5F4DF3B7A0635B9A93E49` (not published).
- `npm audit --omit=dev --audit-level=high`: zero vulnerabilities.
- Screenshot dimensions/hashes and known differences are recorded in `docs/ai/STATUS.md`.
- Independent authentication and UI/build/documentation reviews found no P0. Three confirmed P1 findings were fixed: non-overridable authenticated transport defaults, removal of restore/network behavior from ordinary Fast/Full, and complete fixture-only production-module exclusion. Both reviewers rechecked the fixes and reported no remaining P0/P1.
- Frozen `chat-ui-v1` files were not modified and all four SHA-256 values still match.
- `start-web-dev.cmd` now starts Vite and opens the formal local login at `/`; fixture execution is explicit automation-only. `deploy-web.cmd` remains the deliberate large-version path and does not run on save.
- The Slice 1 fixed-entry deployment succeeded at `https://hklight.2000521.xyz/relaycove-web/`: Nginx backup `20260812T073422Z`, Slice 1 release `20260812T073929Z-1374985197ac-worktree`, public Chromium console 0/0 and strict asset/cache/security checks passed. The later Slice 2 release is recorded below. Official Zulip `/` and legacy `/relaycove/` remain outside the new prefix.
- No real credential, authenticated API call, message write, commit, push or tag occurred.

## Slice 2 local evidence — 2026-08-12

- `npm run typecheck`: passed.
- `npm run test:unit`: 48/48 passed across 9 files, including current-user mismatch/no-persist, distinct authentication/profile email preservation, register/narrow/event/send/queue forms, reducer authorization and unread cleanup, ambiguous/hanging POST one-attempt recovery, bad-queue/restart rebuild with jitter, mark-read failure preservation and stop/start lifecycle races.
- `npm run test:e2e`: formal fake-HTTP journey plus UI/responsive/visual scenarios 5/5; production fixed-subpath preview 1/1; console error/warning guard clean apart from explicitly classified lifecycle cancellation of the event long-poll.
- Production bundle before gzip: JavaScript `272.68 kB` (`83.82 kB` gzip), CSS `24.22 kB` (`5.47 kB` gzip); icons/dependencies remain bundled and fixture content remains excluded.
- Formal fake-HTTP screenshot: `artifacts/web/screenshots/formal-client-fake-1440-light.png`. It is protocol/UI evidence only, not a real-account Realm acceptance.
- Final `pwsh ./scripts/verify.ps1 -Mode Fast` and `-Mode Full` both passed after the last lifecycle fix: .NET Debug/Release 135/135, Web 47/47, typecheck/build, Playwright 5/5 and deployment-path 1/1. Independent authentication, protocol/session/outbox, UI/test/document and deployment reviews report no remaining P0/P1.
- The authorized versioned sync deployed release `20260812T085727Z-1374985197ac-worktree` (archive SHA-256 `D043E6ADF8B5F931CBD18B619419AEBBCBC0589503A0A91A932C57DA4A1CB1AB`) to `https://hklight.2000521.xyz/relaycove-web/`. Public Chromium showed 0 errors/warnings; local and public `index.html`, JS, CSS and favicon hashes match. No real credential or message write was used.

### Post-deployment authentication hotfix — 2026-08-12

- Public access logs proved the reported logout sequence: `fetch_api_key` and the first `users/me` returned 200, then the restored session used the different `users/me.email` profile address as the Basic username and received 401 before register.
- `WebAuthService` now retains the canonical `fetch_api_key.email` for Basic authentication and persists `users/me` only as authoritative user ID/full name. Unit and Playwright regressions deliberately return different authentication/profile addresses and verify login, refresh restore and later authenticated requests keep the former.
- A narrowly scoped target-Realm protocol check using the user-selected member account returned 200 for `fetch_api_key`, `users/me`, `register` and test-queue deletion. A separate fresh headless Chromium then loaded the deployed UI, stayed signed in, completed identity/register/topic and one initial history read with 200, opened/cancelled the event long-poll, logged out with queue deletion 200 and recorded zero console issues. No send or mark-read request occurred, and no message content was written to evidence.
- Final Fast/Full passed with .NET Debug/Release 135/135, Web 48/48, Playwright 5/5 and deployment-path 1/1. Independent authentication review found no P0/P1.
- Hotfix release `20260812T092322Z-1374985197ac-worktree`, archive SHA-256 `F0F83C1EE24E3BC618F1D43A71049B717D7101B7B585BAE5A267328639903B59`, was current when that hotfix closed and remains retained for rollback. Its public HTML/JS/CSS/favicon byte-matched its local `dist`; Nginx and the legacy service remained active. Event-delivery continuity, read-state mutation and message-write Live gates remain open.

## Slice 3 completion evidence — 2026-08-12

- Final `pwsh ./scripts/verify.ps1 -Mode Fast`: passed with zero .NET warnings/errors, Debug 135/135, Web typecheck, deployment-tool regression, unit 63/63 and production build.
- Final `pwsh ./scripts/verify.ps1 -Mode Full`: passed on the final code/text tree; Debug/Release 135/135, Web unit 63/63, Playwright 6/6 plus deployment path 1/1, and local Windows package SHA-256 `362D5D19995DA0CD5ED933641B383DD266B24077C477DABAA3A9E127AA932A0F`.
- Production bundle before gzip: JavaScript `295.77 kB` (`90.61 kB` gzip), CSS `29.88 kB` (`6.43 kB` gzip); no runtime CDN or fixture marker/import.
- Evidence adds `message-actions-1024-light.png`, `image-preview-1024-light.png`, `composer-image-1440-light.png` and the public formal-login capture. Exact dimensions and hashes are recorded in STATUS.
- Independent reviews confirmed and closed two P1 findings: logout now invalidates and aborts an in-flight image submission before session cleanup, and per-message/concurrent/total Blob budgets prevent unbounded image reads. Re-review reported no remaining P0/P1.
- Current fixed-entry release is `20260812T105302Z-1374985197ac-worktree`, archive SHA-256 `DB1A2948B2E70E3CE2F30104E44E46E18E99E9C52CA7ADE8E94E243E9CFAE8E3`. Fresh anonymous Chromium returned 200, rendered the corrected formal-client login copy, logged no error/warning, had no horizontal overflow and byte-matched the deployed JavaScript to local `dist`.
- All media/upload/send automation used fake HTTP. No real account was entered during Slice 3 deployment verification; no real upload, send, mark-read, commit, push or tag occurred.

## Stage 22M slices

1. **Slice 1 implemented:** native tokens/window, componentized responsive shell and light/dark resource wiring;
2. **Slice 1 foundation implemented:** navigation and keyed `ClientState` projection, including authoritative unread and A→B channel-browse regression coverage;
3. **Native message presentation implemented:** virtualized messages, own/other alignment, date/unread dividers, quotes, avatars, reactions, image/file cards, connection/outbox/mutation state and explicit recovery wording;
4. **Composer and interaction parity implemented:** multiline per-conversation drafts, exact send snapshot semantics, Unicode emoji, attachments, local loaded-state search, known-user new DM, message menu, quote/copy/permalink, reaction/edit/delete/star and modal focus behavior;
5. **Shared native contracts implemented:** `Core → Zulip.Client → Data → ClientSession → ShellViewModel`, including same-Realm controlled media, upload, message mutation lanes, unknown-result freeze and SQLite v2 reaction/star/avatar persistence;
6. **Partially evidenced:** real Windows light/dark full-window captures at exact 1440×900 and 1024×768 equivalent DIP on `DISPLAY2` 150%, plus pointer/UI Automation menu/focus/settings checks. Real Realm writes, 100%/200%, full manual keyboard/high contrast, long text/list and scroll-anchor acceptance remain open.

Proposed MAUI landing points remain:

| Responsibility | Target |
|---|---|
| views | `src/RelayCove.App/Views/` |
| controls | `src/RelayCove.App/Controls/` |
| UI state/projectors | `src/RelayCove.App/ViewModels/` |
| Windows behavior | `src/RelayCove.App/Platforms/Windows/` |
| native tokens | `src/RelayCove.App/Resources/Styles/` |
| tests | `tests/RelayCove.App.Tests/` |

## Stage 22M / Slice 1 evidence — 2026-08-13

- Isolated worktree `E:\WorkSpace\RelayCove-Stage22M`, branch `codex/stage-22m-native-shell`, base/HEAD `53a4f1a643031d9eef801e52ab8b20456ea8773c`. The primary worktree was read-only; no Web/frozen-baseline/deployment source changed.
- Implementation spans `RelayCove.App`, shared `RelayCove.Core` contracts/session, `RelayCove.Zulip.Client`, `RelayCove.Data` schema v2 and deterministic tests. The UI remains pure native XAML/ViewModel/Behavior/Windows adapter; no WebView/server/BFF/proxy/second backend was added.
- `ShellViewModel` projects subscriptions/topics/recent DMs/known users/messages/unread/connection/outbox/mutation state from `IClientSession`. The session adds reaction/edit/delete/star/upload and controlled media without bypassing Core. Per-message mutation lanes freeze on unknown outcomes and never automatically retry a non-idempotent write.
- Native interaction includes loaded-workspace search, known-user single/group/self DM, quote/copy/message-link/ID, right-click/`Shift+F10`/touch menu, 24-emoji Composer/reaction pickers, own-message edit/delete confirmation, attachment drafts, image viewer/download, themes and device-only appearance preferences. Server-wide search/mentions, complete membership/presence/common-channel data, saved-message list and channel management remain explicitly unavailable.
- Restore, password login, mark-read, logout and cache paths remain. Text/attachment sends are canonical-conversation keyed and exact-snapshot safe; upload and send are separate one-attempt stages. No real Realm request or write was executed for this Stage 22M handoff.
- App tests passed 36/36. Final Full passed after fixing all 13 Release XamlC explicit-source binding failures with strongly typed ViewModel/control sources and keeping compiled bindings enabled: Core 85/85, Zulip.Client 40/40, Data 17/17 and App 36/36 passed in both Debug and Release (178/178 per configuration), plus deployment-template regression, Web typecheck/unit 63/63/build, Playwright 6/6 and deployment path 1/1. The Windows package was generated with zero warnings/errors; `Live` did not run.
- Independent review closed earlier A→B selection, compact-details focus and screenshot-evidence P1s. Final architecture/security review found no confirmed P0/P1; final UI review found one stale-documentation P1, fixed by this current capability/test/screenshot record.
- The final combined-main Full passed after integrating both owning commits: Debug/Release .NET 178/178 per configuration, Web typecheck and 86/86 unit tests, production build, Playwright 6/6 plus deployment path 1/1, Windows package generation and zero build warnings/errors. `Live` did not run.

Native screenshot evidence is Debug-only, deterministic and no-network: `RELAYCOVE_NATIVE_UI_PREVIEW=1` selects a `#if DEBUG` in-memory `IClientSession`. Send/reaction/edit/delete/star/upload interactions modify only its local memory and cannot reach a Realm. `PrintWindow(PW_RENDERFULLCONTENT)` rendered only the complete RelayCove top-level window on `DISPLAY2`, 150%/DPI 144, dated 2026-08-13:

| Evidence | Target/equivalent DIP | Actual PNG pixels | SHA-256 |
|---|---:|---:|---|
| `artifacts/maui/screenshots/native-shell-wide-display2-150pct-light-stage22m-parity.png` | 1440×900 | 2160×1350 | `B8A38A4C8A93B7234D1557A10EAC062C2ADACD9D9D2D766021F8613B4DDDF104` |
| `artifacts/maui/screenshots/native-shell-compact-display2-150pct-light-stage22m-parity.png` | 1024×768 | 1536×1152 | `7F194E2917C2788CCD42289720AC09D2B9DC1F8F0AC0A8D104FB4330E6A72E8C` |
| `artifacts/maui/screenshots/native-shell-compact-display2-150pct-dark-stage22m-parity.png` | 1024×768 | 1536×1152 | `CD60F951E394D569AFB73CA1870240B257C283DFDF95C7F8A472AC617EF95F12` |
| `artifacts/maui/screenshots/native-message-menu-compact-display2-150pct-dark-stage22m-parity.png` | 1024×768 | 1536×1152 | `6C8A2FB72E4DED1CD86CD26FF91EF1EB54CC034DDD1316F779DCB15A705B3E3C` |

The captures verify startup and visible native shell composition at both target sizes, light/dark resources, details, quote/reaction/image presentation, Composer and the complete message menu, with no visible horizontal clipping at 1024×768. Real pointer/UI Automation checks opened the menu, focused its first action, closed back to the trigger, navigated settings and changed theme. They do not verify real Realm state/writes, 100%/200%, complete manual keyboard/high contrast, long-list anchors, package/sign/install or clean VM. Stage 21 Live, dedicated manual password login and clean Windows VM gates remain explicitly unverified.

## Review and completion

Authentication, protocol, synchronization, outbox and browser credential changes require independent read-only review. Confirmed P0/P1 findings must be fixed and the affected narrow tests plus Fast/Full rerun.

The active Stage 22W delivery is complete only when:

- frozen `chat-ui-v1` files still match all four recorded SHA-256 values;
- Web typecheck, unit, production build and all Playwright scenarios pass;
- Fast and Full pass on the current tree;
- independent review has no unresolved confirmed P0/P1;
- STATUS reports screenshots, implemented formal message synchronization, the exact narrow scope of real-account evidence, remaining capability/Live gates, Stage 21 external gates and final Git state;
- any server synchronization is explicit, versioned and verified; no commit, push, tag, real message send or read-state write occurs in this handoff.
