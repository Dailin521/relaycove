# Stage 24 — MAUI 产品化交互与状态一致性收口

- Status: Stage 24.2 private-chat switching, stable-row cache and native-bottom arbitration follow-up implemented locally; App Debug Rebuild passed with 0 warnings and 0 errors; awaiting a fresh manual retest
- Branch/worktree: `codex/stage-24-2-private-chat-switching` under `E:\WorkSpace\RelayCove-Stage22MParity`
- Baseline: `67fcab4`
- External effects: no Realm access/write; Stage 24.1 was merged through PR #2, while this follow-up remains uncommitted, unpushed and undeployed

## Scope

1. Fix own-message unread badges/dividers and automatic read behavior.
2. Make both switched and repeated conversation activation fetch newest without an empty intermediate frame.
3. Stabilize DM/channel summaries, row identity and account-scoped avatar loading.
4. Implement native Composer pointer resizing and actual 96-DIP bottom-distance behavior.
5. Make channel/topic selection, empty-channel topic creation, realtime topic movement and archive/removal behave like a normal client.
6. Add local conversation filtering, repeated row activation, an 820-DIP layout tier and continuous UI preferences.

Excluded: Web changes, WebView, BFF/proxy/server work, authentication redesign, new Zulip APIs, administrator channel operations, presence, notifications, automatic Live writes, commit/push/deploy.

## Implemented

- Stage 24.2 synchronously projects a hit from Core's bounded in-memory conversation window from the explicit DM click path, rather than waiting for the queued state projection. Equivalent SQLite/network pages no longer publish another message-state repaint; a second click on the same still-pending DM is coalesced, while A/B switching continues to cancel the superseded generation.
- Activation scrolling now uses a cached target as soon as the selected generation exposes one. The activation intent remains alive during authoritative refresh, but a replacement bottom request is published only when the merge contributes a genuinely newer message ID.
- Programmatic realization and native scrolling no longer feed automatic older-page loading or prepend-anchor sampling. Once the requested latest item is materialized, the Windows view moves the native ScrollViewer to its real bottom without animation and verifies the target/bottom relationship on later layout frames.
- Direct-message rows are stable observable instances: selection, unread, preview and timestamp changes update in place instead of replacing the row and its avatar control. Message presentation rows use the same keyed in-place model and an App-side 12-conversation LRU, while avatar cache hits reuse the same in-memory `ImageSource`.

- Stage 24.1 replaces the Composer Button resizer with a neutral 16-DIP native `ContentControl` handle. Windows routed pointer/key events use `AddHandler(..., handledEventsToo: true)`, stable `XamlRoot` coordinates and one capture-release path for release, cancel, capture loss, focus loss, window deactivation and unload.
- Channel/topic/direct rows now have one explicit tap path and stable-key `IsSelected` projection. Channel activation waits for authoritative topics, selects the run-local remembered topic or most active topic, and exposes a real empty-channel state without opening a modal. Pending/error/empty navigation hides old content and gates Composer.
- App scrolling now uses a conversation/generation/target/reason request with acknowledgement. The native view waits for ItemsSource, loaded handler, valid extent and a laid-out target container, retries on layout, verifies the bottom before acknowledgement, and arbitrates explicit bottom requests over generation-bound prepend anchors.
- Message content uses Web-equivalent 18/20/16 insets, a separate 16-DIP scrollbar safety column, the Web 76%/690 and narrow 90% row caps, and no empty opposite-avatar slot. Layout updates refresh the actual 96-DIP bottom distance and preserve pagination anchors by message ID plus DIP offset unless real pointer/wheel/keyboard input takes control.
- Sending now carries the conversation captured with the draft into `IClientSession`; `ClientSession` validates it inside the command gate before creating an outbox entry, so an attachment upload followed by navigation fails closed instead of sending the old draft to the new conversation.
- The formal Web startup policy is now mirrored: restore selects the most recent DM first, otherwise the most active known topic. With no selectable conversation, the native chat retains the full header/message-empty/disabled-Composer skeleton instead of a blank white panel.
- Core keeps an account/session-scoped LRU of at most 12 bounded conversation windows. Returning to an already visited DM swaps its in-memory window immediately while SQLite and newest-history reads revalidate in the background; cache clearing, logout and account reset also clear these memory windows.
- Cross-conversation message projection now raises one collection Reset instead of exposing every Insert/Remove intermediate frame. Same-conversation refreshes reuse presentation-equivalent `MessageItem` rows, preventing avatar/media controls and message containers from being recreated when the authoritative page is unchanged.

- Core normalizes own messages as read before any reducer/cache path. Latest activation owns a generation and cancellation source, keeps current content visible, requests 50 newest messages even for the same conversation, and marks only the still-current displayed unread range.
- Automatic mark-read failure no longer turns a successful latest page into `offline/history_failed`. Unauthorized remains fail-closed; ordinary gateway failure keeps unread state; local read-flag cache failure reports a separate fault.
- `ConversationSummary` projects each conversation's latest cached message from the existing SQLite index. Normal events update it incrementally; delete/move/edit/flag paths query only affected conversation keys. Window-external topic delete/move similarly re-reads only affected channel/topic keys.
- Navigation consumes summaries and preserves stable keys through refresh. Avatar loading skips unchanged source/account keys; blob cache keys include `AccountId`.
- Composer uses native Windows pointer capture and cleanup, clamps 72–300 DIP, retains keyboard adjustment and does not overwrite a larger user height when attachments appear.
- The native viewport passes the real bottom distance to the 96-DIP policy. Near-top paging remains single-flight and existing ID/DIP prepend anchoring remains in place.
- Channel activation restores the last topic for that channel in the current run, otherwise opens its most active topic. Empty channels open a channel-bound new-topic flow. New conversation supports private-message and subscribed-channel topic modes.
- Local conversation filtering covers channel/topic/direct rows. Same-row taps explicitly reactivate the conversation. Font size and conversation width persist continuous clamped values with legacy enum fallback; 820 DIP uses the intermediate native rail.

## Narrow verification

- Stage 24.2 ran no automated tests, Fast, Full, Live, previews or PrintWindow. The exact App Debug Rebuild passed with 0 warnings and 0 errors in 13.07 seconds: `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -t:Rebuild`.
- The subsequent native-bottom arbitration Rebuild attempt did not complete: the still-running `RelayCove.App` PID 34276 and Visual Studio PID 13928 locked the App output, producing 89 warnings and 6 `MSB3027`/`MSB3021` copy errors after retry exhaustion. No process was terminated; rerun the exact command after stopping the app.
- After the app was closed, the final stable-row/cache-first exact App Debug Rebuild passed with 0 warnings and 0 errors in 7.56 seconds.
- A Debug-only, network-disabled `dm-cache-switch` preview now seeds 120 messages for Maya and 120 for Alex, then executes A -> B -> A through the production activation path. PrintWindow captured the tracked HWND on `DISPLAY2` with `InputAutomation: None`; 1440, 1024 and 640 DIP final states all showed message 120 at the bottom, including a tall multiline final row.
- The first 1024-DIP capture exposed that a later viewport/extent reflow could invalidate an already-acknowledged bottom position. `MessageListView` now compares native layout metrics and follows the new bottom only when the prior real distance was <=96 DIP. Fixed 1024 and 640 captures keep the complete final row above Composer; two delayed 640 captures are pixel-identical. The tracked process remained responsive.
- No automated tests, Fast, Full, Live, Realm access or input automation ran. After the visual fix, the exact App Debug Rebuild passed with 0 warnings and 0 errors in 7.07 seconds.

- The previously recorded Stage 24 App/Core/Zulip.Client/Data fake results predate Stage 24.1 and were not rerun.
- Per the user's expedited follow-up instruction, Stage 24.1 adds/runs no tests and does not run Fast, Full, Live, previews or PrintWindow.
- Stage 24.1 App Debug Rebuild passed with 0 warnings and 0 errors using `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -t:Rebuild`.
- Frozen `chat-ui-v1` remains unchanged; no actual secret was added.

## Independent review

- Stage 24.1 received two independent read-only reviews: Composer/navigation/XAML and scroll lifecycle/layout. Confirmed P1 findings covered cross-conversation attachment sends, layout-stale bottom distance, incomplete DIP anchor verification and user-scroll/anchor arbitration; the production paths were corrected and sent through read-only re-review.

- App/UI review covered pointer capture, 96-DIP threshold, paging anchor behavior, repeated activation, filtering, summary/avatar stability, empty-channel topic flow, 820 layout and continuous preferences; no remaining confirmed P0/P1.
- Protocol/session/Data review found and closed two P1 classes: mark-read errors contaminating a successful history load, and window-external message moves/deletes leaving topic projection stale. Both have deterministic regressions; final review found no remaining confirmed P0/P1.

## Manual incident follow-up — 2026-08-14

- Manual Windows testing found three release-blocking behaviors: the restored shell initially showed a blank chat surface instead of Web's automatic initial conversation/empty skeleton; DM activation visibly refreshed, scrolled and flashed; repeated A/B DM activation reloaded cache/network work instead of returning from an in-memory window.
- A rapid DM-switch run entered a confirmed UI busy loop in `RelayCove.App` PID 54392. Windows reported `Responding=False`; a two-second sample consumed 1.46875 CPU seconds, thread count was 154–155, and working set grew from about 638 MiB to 741 MiB and later 1,254,408,192 bytes.
- The direct infinite-loop trigger was scroll acknowledgement requiring the whole target container to fit inside the viewport. A tall final message can never satisfy that condition, so `ScrollTo(End)` and `LayoutUpdated` continuously retriggered each other. Verification now requires target/viewport intersection plus <=2 DIP bottom distance, with 12-attempt bursts that suspend until extent/viewport/offset actually changes.
- State-change projection amplified the incident: every cache/history snapshot was queued independently on the UI dispatcher, canceled navigation still performed a synchronous full projection, and selection history was not linked to the navigation token. Projection is now latest-only/coalesced, canceled paths do not synchronously repaint, and superseded history is canceled at the Core request.
- The first rebuild attempt during the incident failed only because the still-running PID 54392 and its Visual Studio debugging session locked the output DLLs (`MSB3061`, `MSB3026`, `MSB3027`, `MSB3021`; 89 warnings/6 errors). After explicit user confirmation the app process was terminated, without closing Visual Studio. Subsequent exact App Debug Rebuilds passed with 0 warnings and 0 errors; the final cache-first build completed in 7.34 seconds.
- No automated tests, Fast, Full, Live, Realm access, preview or PrintWindow run was performed for this manual follow-up. The final cache-first behavior has not yet received a fresh user manual pass.

## Still unverified

- Formal `Windows Machine` manual checks for DM red dots, same-row newest refresh, avatar stability, Composer drag and multi-topic/empty-channel behavior.
- Fresh real-session startup automatic selection/empty skeleton, rapid user-driven A/B DM switching without blank frames, single-snap bottom placement, unchanged-row stability and real cache-hit latency from the 12-window in-memory cache. The deterministic Debug preview proves only its settled final state, not frame-by-frame transition smoothness.
- Real viewport anchor error <=2 DIP under image resizing/edit/reaction and the 200-page long-list scenario.
- Fast, Full, Live, screenshot matrix, Release/XamlC, package hash/install, 100%/200%, high contrast and clean Windows 11 VM.
- Final MAUI UI password login. Stage 21 must remain open until that and the clean-VM gate pass.
