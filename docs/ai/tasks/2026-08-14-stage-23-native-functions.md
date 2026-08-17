# Stage 23 — MAUI 普通用户全功能收口

- Status: Historical — Slice commits and their final Live/Full evidence are integrated in current main; this file is not the active work order.
- Branch/worktree: `codex/stage-23-native-functions` under `E:\WorkSpace\RelayCove-Stage22MParity`
- Baseline: local commit `8d1a6e5` (`feat(maui): complete native parity and real Zulip flows`)
- External writes: explicitly authorized isolated Stage 23 Live passed 3/3; no push, merge or deployment

## Scope

1. SQLite v3 incremental message cache and bounded native history window.
2. Server message search and the current account's saved-message view.
3. Existing-channel discovery, join, unsubscribe, mute and pin for the current user.
4. Final real MAUI UI acceptance, Full, independent review and delivery evidence.

Excluded: Web changes, WebView, BFF/proxy/server changes, authentication redesign, channel creation/rename/archive, member/permission administration, presence and typing.

## Slice 1 — incremental cache and long lists

- `IAccountStore` now exposes `StoreMessagePageAsync` and `QueryMessagePageAsync`; page queries fetch `limit + 1` and return `HasOlderInCache`.
- SQLite schema v3 adds the reaction message lookup index. Migration is transactional and preserves v2 messages, reactions, flags and outbox-independent cache state.
- Account restore no longer loads every cached message. Register refresh preserves subscribed-channel history while replacing authoritative subscription/user/topic/unread metadata.
- History pages UPSERT only their messages and replace reactions only for those message IDs. Realtime message, flag, reaction, subscription, user and topic events update affected rows rather than reading/deleting/reinserting all messages.
- `ConversationHistoryState` exposes generation, loading/error, oldest-known, cache-older and oldest-loaded ID. Initial/older pages are 50 messages, selected memory is bounded to 250, repeated older requests are single-flight and stale conversation responses cannot project into a new selection.
- Network history always uses the anchor captured before consulting cache. Cached gaps therefore cannot skip a server page; cache results are immediate display/merge inputs only.
- Native `CollectionView` triggers older loading near the top with debounce and keeps the manual accessible action. Prepend recovery uses stable message ID plus a Windows viewport DIP correction; near-bottom arrivals follow automatically, otherwise a new-message button is shown.

### Current narrow evidence

- Slice Fast: 0 build warnings/errors; Core 95/95, Zulip.Client 41/41, Data 21/21 and App 83/83 (240/240 total).
- Web deployment-template checks, typecheck, 86/86 unit tests and production build also passed without changing Web source.
- The deterministic Data suite stores 10,000 messages, pages all 5,000 messages in one conversation without duplicates and checks 50-message page-write p95 <= 150 ms on this development machine.
- No Live, real credential, Realm request, screenshot, Full, package or clean-VM acceptance ran for this Slice.

## Remaining

- Slice 1 and Slice 2 are fixed as separate local rollback commits; Slice 3 is ready for its own local rollback commit.
- After Slice 3 is fixed, proceed to the explicitly authorized isolated real-Realm acceptance without using a business channel.
- Final native real-UI password login, long-list real-window acceptance, package installation and clean Windows 11 VM remain open. Stage 21 is not complete.

## Slice 2 — server search and saved messages (local, 2026-08-14)

- `GET /api/v1/messages` now has separate structured search and saved-page contracts. Search sends the `search` narrow; saved sends `is:starred`; both use `anchor=newest` for the first page, an older exclusive anchor for subsequent pages, and always request raw Markdown (`apply_markdown=false`, `client_gravatar=false`, `allow_empty_topic_name=true`). Match HTML is not mapped, rendered, or persisted.
- Search/saved pages are transient `MessageQueryPage` values and never enter SQLite or the bounded selected-conversation window. Search input debounces for 300 ms, Enter runs immediately, old generations are cancelled, and an empty query retains the local search/navigation results without issuing a server query. Search and saved paging remain explicit user actions; 401, 429 and network failures map to state/error text without automatic retry.
- Saved is now a formal native page with refresh, older-page loading, message jump and unstar action. A successful unstar removes the row. The minimal Core observer reports only message IDs plus delete/starred state: external unstar/delete removes known saved rows and external star marks the page stale for refresh. Unknown mutation outcomes remain visible and do not optimistically remove a saved row.
- `OpenMessageAsync` uses a dedicated raw-Markdown around-anchor request (25 before, anchor, 24 after), verifies `found_anchor`, and uses account/conversation generation plus the session lifecycle token to discard late results. It does not fall back to newest when the target is absent.

### Current narrow evidence

- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore` passed with 0 warnings/errors.
- Core targeted regressions passed 2/2 (old search cancellation/non-persistence; found-anchor around-message context). Zulip.Client targeted protocol tests passed 2/2 (search narrow/raw Markdown/no match HTML; saved narrow/paging).
- Before Slice close, no Full, Live, real credential/Realm request, package, screenshot, clean-VM or manual MAUI login ran. No Web source, deployment or server state changed.

### Slice 2 P1 follow-up (local, 2026-08-14)

- Search and saved now own independent CTS/generation lanes. Each captures account, run identity and epoch; stop/logout/reset/start invalidates both before a late success or HTTP error can affect a new session. Internal cancellation remains silent in the UI.
- Older search/saved pages require `found_anchor`; a missing anchor preserves displayed rows, clears the guessed cursor and requests an explicit refresh. Saved clears immediately on account change, and refresh/load-more retain only their current generation.
- Enter starts an immediate server search unless a result was explicitly selected with navigation keys; closing the dialog restores focus to the search button. Server rows precede local rows and retain all loaded server pages rather than being dropped by a local cap.
- Final Slice Fast passed with 0 build warnings/errors: Core 98/98, Zulip.Client 43/43, Data 21/21 and App 85/85 (247/247 total); Web deployment-template checks, typecheck, 86/86 unit tests and production build also passed. The App regression loads two 50-message server pages and verifies all 100 remain visible.
- Independent protocol/Core and App/UI re-review found no remaining P0/P1 after the request-lifecycle, account-isolation, missing-anchor, Enter, error-state and pagination findings were fixed.

## Slice 3 — channel self-service (local, 2026-08-14)

- Status: implemented locally and passed the Slice Fast gate; no commit, push, merge, Realm request, Web change, deployment, Full or Live execution.
- `ChannelSummary` maps `GET /api/v1/streams` using raw `description`, `is_archived`, and nullable `subscriber_count`; `rendered_description` is intentionally ignored. Subscription preferences use official `PATCH /api/v1/users/me/subscriptions/{id}` properties `is_muted` and `pin_to_top`.
- The browser can only join catalog items. `SubscribeToChannelAsync(channelId)` re-fetches the catalog and checks ID + exact current name + non-archived before POSTing Zulip's official, name-based `users/me/subscriptions` API. A missing or renamed entry fails before POST, because the server API otherwise permits creating names.
- Add/remove and stream rename/archive continue to reduce subscription state; subscription preference events update muted/pinned. Confirmed responses alone project a join/preference change; rejected/unknown/401/429/network failures are not retried or optimistically applied.
- Formal MAUI adds a browse/join overlay, channel detail mute/pin/leave actions, pinned-first/muted-weakened projection, and close/Escape focus behavior. Subscriber count is displayed only when supplied by the catalog.

### Current narrow evidence

- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore`: 0 warnings/errors.
- `dotnet test tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj -c Debug --no-restore`: 45/45.
- Final Slice Fast passed with 0 build warnings/errors: Core 102/102, Zulip.Client 45/45, Data 21/21 and App 87/87 (255/255 total); Web deployment-template checks, typecheck, 86/86 unit tests and production build also passed.
- At Slice 3 close, manual MAUI UI/focus at real narrow widths, screenshot/visual review, accessibility, package/clean VM, Full and Live/real Realm behavior were still unverified; later final evidence is recorded below.

### Slice 3 P1 follow-up (local, 2026-08-14)

- Core gates catalog queries by account/run/catalog generation, gates subscribe/preference writes by account/run/session epoch, cancels catalog work on lifecycle transitions, and serializes concurrent joins per channel. A catalog refresh cannot discard a confirmed write, while late cancellation, success and gateway failure cannot project into a replacement session.
- Subscribe now requires a parseable official structured response with the exact verified current name in `subscribed` or `already_subscribed`; a malformed successful HTTP response is treated as a protocol failure. `ChannelSummary` also carries private/subscribed/color catalog metadata while subscriber count remains nullable.
- SQLite schema v5 stores `is_muted`/`is_pinned` plus register-provided `color`, persists preference events, and conditionally migrates old schemas without assuming legacy fixture columns. `GET /streams` does not invent subscription/color data; Core merges those fields from current authoritative subscriptions by ID. The migration test verifies the preference columns.
- The browser cancels the real catalog request and ignores stale completion/error after close/logout/account change, clears its collection on close, and restores focus to the browse trigger. Projection changes notify the mute/pin action labels.
- Independent protocol/Core/Data and App/UI re-reviews found no remaining P0/P1 after the close-recursion, cancellation, lifecycle, response-shape, persistence, focus and 401 findings were fixed. Full/manual MAUI/screenshot/package/clean-VM remain unrun; the later isolated Live result is recorded below.

## Stage 23 isolated Live acceptance (2026-08-14)

- The tracked gate now requires a separate Stage 23 approval, exact joinable-channel ID/name approval, two-account allowlist and the existing explicit write confirmation before any test starts. The ignored runner additionally requires an external confirmation before reading the credential archive or issuing HTTP, and disables redirects for every authenticated PowerShell request.
- Tracked preflight verifies the two private probes and one public join probe against the Realm before every write, including exact names, privacy/archive state, three distinct IDs and exactly DAL/zhang as subscribers. All register queues are deleted with an independent cleanup token.
- Final Live passed 3/3: search/saved/fresh around-anchor open; star/unstar; mute/pin toggle and authoritative restore; private unsubscribe and restore; public dedicated `relaycove-join-e2e` unsubscribe, catalog discovery and rejoin. Cleanup completed without a reported restoration failure.
- A private channel cannot be discovered after an ordinary user unsubscribes, so the join probe is deliberately public/non-archived but has exactly the two approved subscribers. It is isolated from business channels and does not add channel-creation capability to the product.
- Still open: final manual password login through the native MAUI UI, real-window long-list/anchor acceptance, accessibility/high-contrast/100%/200%, package installation and clean Windows 11 VM.

## Final candidate gate (2026-08-14)

- Release XamlC found five new explicit-source bindings that Debug accepted. They were corrected through the existing strongly typed `RootPage.ViewModel` bridge; compiled bindings remain enabled. Independent App review found no remaining P0/P1.
- Final `pwsh ./scripts/verify.ps1 -Mode Full` passed: Debug/Release each Core 102/102, Zulip.Client 45/45, Data 21/21, App 87/87 (255/255); Web 86/86; Playwright 6/6 plus deployment-path 1/1; zero build warnings/errors.
- Windows package SHA-256: `75B176F07531DAD9D1DEF1412B37778B1B876840ACA4862F663BE8FC586A0994`.
- Full was offline and did not reuse Live credentials. Remaining user/VM gates are manual native password login, real-window long-list/anchor behavior, package installation on clean Windows 11, 100%/200%, high contrast and accessibility.
- For that manual login, select Visual Studio profile `Windows Machine`. The separate `RelayCove Native Preview` profile remains the no-network fixture/Hot Reload entry and must not be used as real Realm evidence.
