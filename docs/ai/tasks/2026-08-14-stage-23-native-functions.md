# Stage 23 — MAUI 普通用户全功能收口

- Status: active; Slice 1 long-list/data foundation passed its local Fast gate and is ready for its rollback commit
- Branch/worktree: `codex/stage-23-native-functions` under `E:\WorkSpace\RelayCove-Stage22MParity`
- Baseline: local commit `8d1a6e5` (`feat(maui): complete native parity and real Zulip flows`)
- External writes: none for Slice 1; no push, merge, deployment or Live run

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
