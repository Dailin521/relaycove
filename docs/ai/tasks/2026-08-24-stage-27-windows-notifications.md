# Stage 27 — Windows native message notifications

Status: user Visual Studio validation passed; local candidate awaiting delivery

## Scope

- Add notifications only to the native Windows MAUI client. Web, presence, typing, background push, Realm configuration and server data are unchanged.
- For accepted realtime messages from another user, provide a Windows system notification, taskbar attention flash and authoritative unread badge when the message is not already visible in the active chat.
- Preserve existing per-conversation mute semantics. Add device-local global notification preferences and do not represent them as Zulip server settings.

## Trigger and authority boundary

- `ClientSession` exposes a narrow optional observer only after a realtime `MessageUpsertEvent` has passed cursor replay rejection, supported-conversation filtering, account-cache persistence and Core projection.
- Register snapshots, cache restore, history pages, send confirmations, old/null replay groups, public channels, legacy named topics and group DMs never become notification candidates.
- The observer still reports a current-user realtime echo because that is an accepted Core event; the App suppresses own-message attention by authoritative current user ID.
- The taskbar badge is never incremented locally. It is replaced from `UnreadState.Total`/`IsTruncated`; any known supported unread count is shown numerically, including when the server says older unread data is truncated. Only a truncated state with no known supported unread count uses the system new-message glyph.

## Final implementation

- `WindowsAppNotificationService` uses Windows App SDK `AppNotificationManager` for system notifications and `BadgeNotificationManager` for the application badge. Because an unpackaged notification identity may not attach that badge to the running HWND taskbar button, the same authoritative count is also rendered through `ITaskbarList3.SetOverlayIcon`. It uses `FlashWindowEx` with taskbar-only, until-foreground attention and stops flashing on real window activation, notification activation or zero unread.
- Clicking a notification restores/foregrounds the existing window and opens the matching conversation when it remains in the supported unified list.
- The notification settings page now persists five device-local choices: system notifications, message preview, taskbar flash, taskbar badge and global do-not-disturb. Do-not-disturb and conversation mute suppress toast/flash but retain the authoritative unread badge.
- An active selected chat suppresses toast/flash while the RelayCove HWND is the real non-minimized Windows foreground window, its message surface is visible and no modal overlay is covering it, regardless of whether the user is at the latest row or reading earlier messages. In-chat unread UI remains responsible for messages not yet viewed. MAUI `OnAppearing`/`Activated` are treated only as delayed read-state recheck triggers: taskbar thumbnail hover/Aero Peek cannot mark a conversation read or stop its flash. Each still-current activation waits 100 ms before the native foreground check so a real restore is not lost to Windows activation timing and a transient lifecycle signal cannot start a read request. Core history loading no longer marks messages read by itself; the guarded App request is the sole automatic path.
- System notifications resolve the sender avatar from the accepted message or authoritative user projection. The remote URL is never given directly to Windows: RelayCove performs the existing authenticated same-Realm media read with a 1 MiB limit, accepts PNG/JPEG only, stores it under an opaque account-isolated SHA-256 directory/name in the local cache, and supplies only the resulting `file:///` URI as a circular app-logo override. Missing, rejected or slow avatars fall back to the application icon without blocking message processing. Successful logout and clear-local-cache operations remove that account's notification-avatar cache.
- Windows registration, policy, badge or Win32 failures fail closed and never interrupt the Zulip event loop. Badge failure attempts to clear a stale platform value and is reported separately from system-notification registration in Settings. The page also states that this is runtime desktop notification, not offline/background push.

## Deterministic evidence

- `dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo` — 151/151 passed.
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage27-active-chat-toast-test/` — 252/252 passed. An isolated output was required because the user-owned Visual Studio process had the default Debug AppHost open; it was not stopped or operated.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:OutputPath=.verify/stage27-active-chat-toast-build/` — passed with 0 warnings/errors.
- Regression coverage includes accepted realtime delivery, replay/register/unsupported exclusion, own message, muted and visible-chat suppression, do-not-disturb, numeric badge and HWND overlay rendering, zero-known truncated fallback, real foreground HWND detection, deferred activation recheck, taskbar-preview hover rejection, guarded-only Core read behavior, notification avatar source projection/local-file restriction and independent local preference persistence.

## Follow-up defect record — unread count and taskbar hover

- Symptom: the taskbar indicator did not communicate the unread quantity, and hovering the taskbar thumbnail without opening RelayCove could make the selected conversation disappear from unread state.
- Root cause: the badge policy preferred an unknown glyph whenever `IsTruncated` was set, even when `UnreadState.Total` contained a known supported unread count. Separately, `MainPage.OnAppearing` and MAUI window activation were trusted as proof of foreground visibility; Windows taskbar preview/Aero Peek can raise lifecycle signals without the user opening the window.
- Rejected approach: lifecycle activation alone remains insufficient, and clearing/marking read on hover would violate the server-authoritative read contract.
- Final implementation: show every known supported unread count numerically; use the glyph only when truncation is known but the supported count is zero. A rejected Windows badge update is surfaced as a separate taskbar status and attempts a best-effort clear instead of silently claiming success. Before suppressing notification, stopping flash or requesting auto-mark-read, compare the RelayCove HWND with `GetForegroundWindow` and reject minimized windows. A repeated activation signal rechecks this native condition so a later real click still performs the pending mark-read.
- Verification: the isolated App test suite passed 240/240 and the isolated App Debug build passed with 0 warnings/errors. The later final Visual Studio result is recorded below.

## Follow-up defect record — post-open unread, visible taskbar count and sender avatar

- Symptom: after a real window restore the selected conversation could remain at unread `1`; the taskbar button still had no numeric marker although another application did; the system toast showed sender text but no sender avatar.
- Root causes: the first native foreground check can run before Windows finishes promoting the HWND, and no later production callback retried it. Core also retained an older history-load auto-read path outside the App visibility gate. `BadgeNotificationManager` can update the unpackaged notification identity without producing a visible overlay on the already-created RelayCove taskbar HWND. Finally, the toast model contained no avatar, and a protected Zulip avatar URL cannot be handed directly to Windows because the Shell has no RelayCove authentication header.
- Rejected approaches: do not clear the local unread count optimistically, mark on history load, treat taskbar hover as foreground, give credentials/remote avatar URLs to Windows, or delay the Zulip event loop while preparing a toast.
- Final implementation: wait 100 ms before the foreground check for each still-current activation and remove Core history-load marking; the existing App gate remains responsible for one expected-conversation read request after native latest-position confirmation. Apply the known count both through WinAppSDK and a directly HWND-bound `ITaskbarList3` numeric overlay. Prepare sender avatars asynchronously through the controlled Realm media API and an account-isolated hashed local file, then use a circular app-logo override; logout and clear-local-cache remove the account directory.
- Verification: Core 151/151 and App 252/252 passed. App Debug build passed with 0 warnings/errors. The later final Visual Studio result is recorded below.

## Follow-up interaction correction — active chat toast suppression

- Symptom: a Windows toast for the same conversation appeared while that chat was already open in the foreground, which duplicated the visible in-app conversation.
- Final implementation: suppress toast and taskbar flash whenever the matching chat surface is genuinely foreground and unobscured, even when the user has scrolled up. This does not mark the message read or clear its badge; the existing latest-position and server-confirmed read gate remains unchanged.
- Verification: App regression and Debug build evidence are refreshed below; the user then confirmed the final active-chat behavior in Visual Studio.

Not run: Fast, Full, Live, packaging, app startup, internal screenshot, system-notification emission, Realm access, commit or push.

## Visual Studio short check

1. Open Settings → Notifications. Confirm all five switches render in separate rows and the Windows status explains whether system notifications are enabled.
2. Keep chat A selected and minimize/deactivate RelayCove, then send multiple messages to A from another account. Confirm one Windows notification appears with the sender's circular avatar, the taskbar button flashes and the taskbar button itself shows the known unread quantity.
3. Hover the taskbar button so its thumbnail/Aero Peek appears, but do not click it. The unread quantity and flash must remain, and chat A must not be marked read.
4. Click RelayCove and open chat A. After the matching chat is really foreground, its latest position is confirmed and Zulip accepts mark-read, both the left-list number and taskbar number should clear; flashing should stop.
5. Keep chat A open in the foreground and receive another message in A, including once while scrolled upward. No Windows toast or taskbar flash should appear; the in-chat unread state may remain until the latest row is actually viewed.
6. Enable global do-not-disturb, then repeat. No system notification or taskbar flash should appear, but the unread badge should still update.
7. Disable do-not-disturb and mute only chat A. Repeat once more; chat A should likewise suppress toast/flash without hiding its unread badge.

Manual result: passed. After the sender-avatar/taskbar/unread corrections, the user's real Windows run exposed one remaining redundant toast while the matching chat was already open. The final foreground-chat suppression was applied, and the user confirmed “没问题” in Visual Studio. Stage 27 remains uncommitted and unpushed pending a separate delivery request.
