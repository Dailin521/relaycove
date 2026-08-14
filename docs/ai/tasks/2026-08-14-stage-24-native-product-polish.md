# Stage 24 — MAUI 产品化交互与状态一致性收口

- Status: Stage 24.1 implemented locally; App Debug Rebuild passed with 0 warnings and 0 errors; no tests requested for this follow-up; awaiting user manual review
- Branch/worktree: `codex/stage-24-native-product-polish` under `E:\WorkSpace\RelayCove-Stage22MParity`
- Baseline: `c694683`
- External effects: no Realm access/write, commit, push, merge, deployment, tag or release

## Scope

1. Fix own-message unread badges/dividers and automatic read behavior.
2. Make both switched and repeated conversation activation fetch newest without an empty intermediate frame.
3. Stabilize DM/channel summaries, row identity and account-scoped avatar loading.
4. Implement native Composer pointer resizing and actual 96-DIP bottom-distance behavior.
5. Make channel/topic selection, empty-channel topic creation, realtime topic movement and archive/removal behave like a normal client.
6. Add local conversation filtering, repeated row activation, an 820-DIP layout tier and continuous UI preferences.

Excluded: Web changes, WebView, BFF/proxy/server work, authentication redesign, new Zulip APIs, administrator channel operations, presence, notifications, automatic Live writes, commit/push/deploy.

## Implemented

- Stage 24.1 replaces the Composer Button resizer with a neutral 16-DIP native `ContentControl` handle. Windows routed pointer/key events use `AddHandler(..., handledEventsToo: true)`, stable `XamlRoot` coordinates and one capture-release path for release, cancel, capture loss, focus loss, window deactivation and unload.
- Channel/topic/direct rows now have one explicit tap path and stable-key `IsSelected` projection. Channel activation waits for authoritative topics, selects the run-local remembered topic or most active topic, and exposes a real empty-channel state without opening a modal. Pending/error/empty navigation hides old content and gates Composer.
- App scrolling now uses a conversation/generation/target/reason request with acknowledgement. The native view waits for ItemsSource, loaded handler, valid extent and a laid-out target container, retries on layout, verifies the bottom before acknowledgement, and arbitrates explicit bottom requests over generation-bound prepend anchors.
- Message content uses Web-equivalent 18/20/16 insets, a separate 16-DIP scrollbar safety column, the Web 76%/690 and narrow 90% row caps, and no empty opposite-avatar slot. Layout updates refresh the actual 96-DIP bottom distance and preserve pagination anchors by message ID plus DIP offset unless real pointer/wheel/keyboard input takes control.
- Sending now carries the conversation captured with the draft into `IClientSession`; `ClientSession` validates it inside the command gate before creating an outbox entry, so an attachment upload followed by navigation fails closed instead of sending the old draft to the new conversation.

- Core normalizes own messages as read before any reducer/cache path. Latest activation owns a generation and cancellation source, keeps current content visible, requests 50 newest messages even for the same conversation, and marks only the still-current displayed unread range.
- Automatic mark-read failure no longer turns a successful latest page into `offline/history_failed`. Unauthorized remains fail-closed; ordinary gateway failure keeps unread state; local read-flag cache failure reports a separate fault.
- `ConversationSummary` projects each conversation's latest cached message from the existing SQLite index. Normal events update it incrementally; delete/move/edit/flag paths query only affected conversation keys. Window-external topic delete/move similarly re-reads only affected channel/topic keys.
- Navigation consumes summaries and preserves stable keys through refresh. Avatar loading skips unchanged source/account keys; blob cache keys include `AccountId`.
- Composer uses native Windows pointer capture and cleanup, clamps 72–300 DIP, retains keyboard adjustment and does not overwrite a larger user height when attachments appear.
- The native viewport passes the real bottom distance to the 96-DIP policy. Near-top paging remains single-flight and existing ID/DIP prepend anchoring remains in place.
- Channel activation restores the last topic for that channel in the current run, otherwise opens its most active topic. Empty channels open a channel-bound new-topic flow. New conversation supports private-message and subscribed-channel topic modes.
- Local conversation filtering covers channel/topic/direct rows. Same-row taps explicitly reactivate the conversation. Font size and conversation width persist continuous clamped values with legacy enum fallback; 820 DIP uses the intermediate native rail.

## Narrow verification

- The previously recorded Stage 24 App/Core/Zulip.Client/Data fake results predate Stage 24.1 and were not rerun.
- Per the user's expedited follow-up instruction, Stage 24.1 adds/runs no tests and does not run Fast, Full, Live, previews or PrintWindow.
- Stage 24.1 App Debug Rebuild passed with 0 warnings and 0 errors using `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -t:Rebuild`.
- Frozen `chat-ui-v1` remains unchanged; no actual secret was added.

## Independent review

- Stage 24.1 received two independent read-only reviews: Composer/navigation/XAML and scroll lifecycle/layout. Confirmed P1 findings covered cross-conversation attachment sends, layout-stale bottom distance, incomplete DIP anchor verification and user-scroll/anchor arbitration; the production paths were corrected and sent through read-only re-review.

- App/UI review covered pointer capture, 96-DIP threshold, paging anchor behavior, repeated activation, filtering, summary/avatar stability, empty-channel topic flow, 820 layout and continuous preferences; no remaining confirmed P0/P1.
- Protocol/session/Data review found and closed two P1 classes: mark-read errors contaminating a successful history load, and window-external message moves/deletes leaving topic projection stale. Both have deterministic regressions; final review found no remaining confirmed P0/P1.

## Still unverified

- Formal `Windows Machine` manual checks for DM red dots, same-row newest refresh, avatar stability, Composer drag and multi-topic/empty-channel behavior.
- Real viewport anchor error <=2 DIP under image resizing/edit/reaction and the 200-page long-list scenario.
- Fast, Full, Live, screenshot matrix, Release/XamlC, package hash/install, 100%/200%, high contrast and clean Windows 11 VM.
- Final MAUI UI password login. Stage 21 must remain open until that and the clean-VM gate pass.
