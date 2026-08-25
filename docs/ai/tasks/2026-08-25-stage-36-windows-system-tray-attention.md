# Stage 36 — Windows 系统托盘闪烁与悬浮预览

Status: completed and accepted by user in Visual Studio

## Scope

- RelayCove 运行期间常驻一个 Windows 系统托盘图标。
- 符合既有 Stage 27 通知门禁的新消息到达后，托盘图标按 500 ms 周期在正常图标与透明图标之间切换，形成类似微信的“消失、出现”提醒。
- 鼠标悬停托盘图标时显示不抢焦点的消息预览，包含发送者/会话标题、正文摘要、发送者头像回退和权威总未读数。
- 左键有未读的托盘图标恢复现有主窗口并打开当前预览对应会话；无未读时只恢复窗口。点击本身不直接清未读或标读；应用关闭行为、任务栏闪烁/徽标和系统横幅保持原边界。

## Diagnosis

- Stage 27 只接入了 Windows 系统横幅、任务栏 `FlashWindowEx` 和任务栏未读 overlay，没有创建 RelayCove 自己的 notification-area 图标，因此托盘区域既不能闪烁，也没有可承载悬浮预览的命中目标。
- Windows 标准托盘 tooltip 只能提供短文本，不能展示头像、两行正文和独立未读徽标；预览必须使用应用自绘、非激活的 Windows App SDK 窗口。
- 被否决的方案包括每个周期反复 `NIM_DELETE`/`NIM_ADD`、只修改 tooltip 和用系统横幅冒充悬浮预览。删除/重加会折叠托盘槽位并可能移动相邻图标；纯 tooltip 不满足预览信息要求；横幅不是鼠标悬浮交互。

## Final implementation

- Windows 通知服务在 MAUI 原生 HWND 可用后创建专用 message-only window，并用稳定 GUID 注册 `NOTIFYICON_VERSION_4` 托盘图标；Explorer 重建任务栏后自动恢复图标。
- 闪烁不删除托盘项，而是原位交替正常应用图标与透明图标。这样视觉上消失/出现，同时槽位、鼠标命中和悬浮预览保持稳定。悬浮期间强制显示正常图标并暂停计时，离开后在仍有提醒请求和权威未读时继续。
- `NIN_POPUPOPEN` 打开 360×112 DIP 的 WinUI 无边框预览，使用 `Shell_NotifyIconGetRect`、当前显示器工作区和 DPI 定位；窗口设置为 tool/no-activate/topmost，不进入 Alt+Tab，也不抢输入焦点。
- 预览先立即显示标题、正文和姓名首字，随后复用受控、账号隔离的通知头像缓存异步替换头像。旧账号/旧消息 generation、超时或头像失败不会覆盖新预览。
- 托盘未读始终从 Core `UnreadState.Total`/`IsTruncated` 覆盖投影，不在本机自增。权威数量下降时清除可能过时的最新消息资料；清零时停止闪烁。仅悬停、恢复窗口或获得焦点不清未读，仍须打开对应会话并通过既有最新位置与服务器确认门禁。
- “任务栏闪烁”设备偏好同时控制托盘提醒闪烁；全局免打扰会停止两者。关闭“系统通知”只关闭系统横幅，托盘预览仍可使用。左键托盘图标复用系统通知的窗口恢复与会话激活路径，不改变应用关闭即退出的行为。

## Deterministic validation

- 聚焦通知、权威未读、免打扰、系统横幅关闭、托盘 tooltip 和 DPI 缩放回归：passed 11/11。
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage36-tray-build-6/` — passed with 0 warnings and 0 errors.

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Follow-up defect record — tray click crash and stale icon suspicion

- Symptom: the first Visual Studio check confirmed the tray surface but clicking its icon terminated RelayCove immediately. The icon also looked like an old RelayCove registration.
- Read-only evidence: Windows Application Error recorded three identical crashes at 14:18–14:19 from the current Debug executable, all with exception code `0xc000041d` (`STATUS_FATAL_USER_CALLBACK_EXCEPTION`) in `KERNELBASE.dll`. This identifies an exception escaping a native user callback rather than a detached legacy process. Git history also confirms the project `RelayCove.ico` artwork itself dates from the initial MAUI rebuild; no newer icon asset exists in the repository.
- Root cause: the notification-area window procedure directly hid the WinUI preview, restored the MAUI window and then returned focus to the tray. Any WinUI/MAUI exception in that unmanaged callback crosses the native boundary and Windows terminates the process. Returning notification-area focus after activating the main window was also unnecessary.
- Final correction: activation notifications now only enqueue one restore action onto the captured UI `DispatcherQueue`; the queued action is exception-contained and no longer returns focus to the tray. The unmanaged procedure itself has a final exception boundary so no managed exception can escape it. Attach first deletes any same-GUID stale Shell registration, then adds the current process item. The visible icon is copied from the current MAUI main-window icon first, with executable extraction only as fallback.
- Additional regression for a throwing platform action plus tooltip/DPI helpers passed 8/8. The corrected App Debug build passed with 0 warnings/errors. User Visual Studio reconfirmation is pending.

## Follow-up defect record — hover produced no preview

- Symptom: after the click-crash correction, the icon blinked normally but moving the pointer over it displayed neither the intended rich preview nor any visible fallback.
- User-provided debugger evidence identified the exact exception: `System.EntryPointNotFoundException`, because the P/Invoke searched for `ShellNotifyIconGetRect` while `shell32.dll` exports `Shell_NotifyIconGetRect` with an underscore. The missing function prevented both preview positioning and the hover-leave tracker.
- Final correction: the declaration now pins `EntryPoint = "Shell_NotifyIconGetRect"` with exact spelling, and cursor-over-icon probing fails closed if a future platform interop error occurs. Both `NIN_POPUPOPEN` and icon `WM_MOUSEMOVE` still enqueue the complete preview path onto the UI dispatcher; pointer tracking closes the preview on leave and normal `NIN_POPUPCLOSE` remains supported.
- A reflection regression now locks the exact native export name. Focused interop/callback/hover/tooltip/DPI regressions passed 11/11. The corrected App Debug build passed with 0 warnings/errors. User Visual Studio reconfirmation is pending.

## Follow-up interaction correction — no preview without unread

- User clarified that the hover card is an unread-message preview, not a permanent tray status panel. A clean authoritative unread state must therefore leave hover silent instead of showing “暂无未读消息”.
- Preview open now requires `UnreadState.Total > 0` or a server-truncated unknown unread state. When authority changes to a clean zero while the card is visible, the controller immediately queues its close in addition to stopping the blink and clearing stale preview data.
- Focused unread-preview and existing tray interop regressions passed 14/14. The final App Debug build passed with 0 warnings/errors; user Visual Studio confirmation is pending.

## Follow-up visual correction — avatar refreshed during pointer movement

- Symptom: with the hover card open, moving the pointer inside the tray icon made the sender avatar visibly refresh/flicker.
- Root cause: every `WM_MOUSEMOVE` requested the same visible state, but the dispatcher reapplied the full show path. That repeatedly rebuilt the avatar `BitmapImage`, updated content, repositioned the AppWindow and called `Show(false)` even though the card was already visible.
- Final correction: preview visibility is idempotent. Repeated visible requests only keep the leave tracker alive; the content, avatar source, position and window show operation are applied only on the hidden-to-visible transition. The visible-to-hidden transition remains single-shot.
- Focused hover-stability and existing tray regressions passed 18/18. The final App Debug build passed with 0 warnings/errors; user Visual Studio confirmation is pending.

## Follow-up interaction correction — tray click opens preview conversation

- User clarified that clicking an unread tray icon must restore RelayCove and open the conversation represented by the current hover preview, instead of stopping at the previously selected chat.
- The controller snapshots the latest unread preview's canonical conversation key at click time and passes it through the same `NotificationActivated` route already used by Windows system notifications. The Shell restores the messages section and activates that supported unified conversation; existing latest-position/server-confirmed read rules remain unchanged.
- A clean no-unread tray click has no preview conversation and therefore only restores the window. If authoritative unread remains but the latest preview identity was invalidated, the client also fails closed to restore-only rather than guessing a conversation.
- Focused tray routing, existing notification activation and tray regressions passed 21/21. The final App Debug build passed with 0 warnings/errors; user Visual Studio confirmation is pending.

## Manual result

- The user validated the final tray behavior in Visual Studio after the native-entry-point, click, hover, unread gating, avatar stability and conversation-navigation corrections, then explicitly requested commit and push.

## Shortest manual check

1. 用 Visual Studio 启动 RelayCove，确认通知区域/折叠区存在 RelayCove 图标；从另一个账号向当前未打开的会话发送消息，确认图标在原位置消失、出现且相邻托盘图标不移动。
2. 悬停图标，确认预览靠近托盘出现，包含头像、发送者/会话、正文摘要和总未读数；预览不激活 RelayCove，也不使左侧未读消失。移开后仍有未读时继续闪烁。
3. 左键有未读的托盘图标，确认主窗口恢复并打开预览对应会话，但未读只在到达最新且服务器确认后清除；清零后托盘图标停止闪烁并稳定显示。无未读时点击只恢复窗口。
4. 开启全局免打扰或关闭“任务栏闪烁”，确认托盘与任务栏都停止闪烁；只关闭“系统通知”时不出现 Windows 横幅，但后台未读仍可在托盘预览中查看。
