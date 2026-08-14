# Stage 23 — MAUI 普通用户全功能收口

- Status: active; Slice 1 is fixed at local commit `037bcf8`, and Slice 2 server search/saved passed its Fast gate and is ready for its rollback commit
- Branch/worktree: `codex/stage-23-native-functions` under `E:\WorkSpace\RelayCove-Stage22MParity`
- Baseline: local commit `8d1a6e5` (`feat(maui): complete native parity and real Zulip flows`)
- External writes: none for Stage 23 so far; no push, merge, deployment or Live run

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

- Independent Data/Core/App review found no remaining P0/P1 after the cache-gap network-anchor regression and empty-message projection crash were fixed; create the Slice 1 local rollback commit.
- Implement server search/saved, then channel self-service, each as a separate local Slice commit.
- Final native real-UI password login, long-list real-window acceptance, Full/package and clean Windows 11 VM remain open. Stage 21 is not complete.

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
