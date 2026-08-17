# Stage 24.2 — 当前可见消息标读与 Composer Enter

- Status: Integrated into `main@4b8dd64`; narrow tests, Fast and Full passed; independent reviews found no P0/P1
- Development branch/worktree: `codex/fix-native-unread-enter` under `E:\WorkSpace\RelayCove-UnreadEnter` (to be removed after integration cleanup)
- Starting point: `origin/main@67fcab46cdd87d5a77eec98dc3970a7333d7c8a2`
- External effects: user-authorized Git commit/push/fast-forward merge to `main`; no credentials, Realm, Live or deployment

## Scope

1. 当前激活窗口已经显示会话并确认列表位于底部时，收到他人实时新消息后发起服务器标读。
2. 服务器确认前保留消息未读线、会话红点和主导航红点；失败不乐观清除。
3. 会话切换、窗口失焦、窄屏会话列表、设置页、遮罩或离开底部时不自动标读。
4. Composer 改为 Enter 发送、Ctrl+Enter 换行；IME 组合输入期间不发送。

## Implementation boundary

- App 只通过现有 `IClientSession`；不新增服务端、BFF、代理、WebView 或第二消息后端。
- `MarkDisplayedReadAsync(expectedConversation)` 在 Core command gate 内重新核对当前会话，避免排队竞态误标新会话。
- 当前会话在消息列表底部时，实时消息的 follow-scroll 不阻塞自动标读：WinUI 的后续滚动确认可能延迟或缺失。手动跳转/激活等其他滚动请求仍须确认；状态清除始终来自服务器成功后 Core 发布的 `read` flags。
- 手动详情栏“标为已读”保留原无参语义。

## Verification

- `dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo --verbosity minimal`：108/108 passed。
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo --verbosity minimal`：104/104 passed。
- `pwsh ./scripts/verify.ps1 -Mode Fast`：passed，Debug build 0 warnings/errors；Core 108/108、Zulip.Client 45/45、Data 23/23、App 104/104（280/280），Web deployment-template/typecheck、86/86 unit、production build 均通过；LiveTests 只编译未执行。
- `pwsh ./scripts/verify.ps1 -Mode Full`：passed，Release 同为 Core 108/108、Zulip.Client 45/45、Data 23/23、App 104/104（280/280），两组 fake-HTTP Playwright 通过；生成的 `RelayCove-2.0.0-alpha.1-win-x64.zip` SHA-256 为 `5154D7749C624C90BAC0FAEBF1D8F64530FA532C32D7F872F246AB34D4B25FC5`。
- 独立只读复核：自动标读并发/session 权威边界与 Composer 键盘/IME 两路均无确认 P0/P1。

## Open gates

- 未运行 Live 或访问真实 Realm；真实 mark-read 写验收仍需独立授权。
- 未运行正式 Windows Machine 人工键盘/焦点验收；确定性测试不能替代真实 IME/窗口行为。
- Stage 21 的最终 MAUI 人工密码登录、包实装和 clean Windows 11 VM 继续保持未验证。

## 2026-08-17 Composer regression correction

- 用户报告原生输入框在最低高度时文本溢出、光标/点击异常、发送后列表闪动以及已发送草稿残留。修正将 Composer 最小和默认高度改为 128 DIP，保留 64 DIP 编辑区和 40 DIP 工具栏；72 DIP 不再是 Windows 原生可达尺寸。透明的 16 DIP 拖拽热区覆盖顶部边框上下附近，布局仅预留 8 DIP，且不显示单独灰色把手。
- 普通 Enter 用 `KeyboardAccelerator` 执行发送，Ctrl+Enter 由原生 `TextBox` 选择范围替换为换行；选择行为只从原生控件投影光标，程序化焦点请求才写回，避免点击位置被旧 ViewModel 值抢回。
- 新消息/发送后的 `RealtimeFollow` 不再进入基于 `LayoutUpdated` 的容器实现重试循环；它只执行一次原生底部滚动并确认请求。回归覆盖同一状态重复发布不重新排队、发送期间状态发布仍清空未改草稿、换行替换选择区和新的 128–300 DIP 下界。
- 本轮 `pwsh ./scripts/verify.ps1 -Mode Full` 通过：Debug 和 Release 各为 Core 109/109、Zulip.Client 45/45、Data 23/23、App 109/109（572/572）；Web deployment-template/typecheck、86/86 unit、production build 与 fake-HTTP Playwright 均通过。新包 `RelayCove-2.0.0-alpha.1-win-x64.zip` SHA-256 为 `117CB3E648EF47500EA2ADE9AA19220E311C0E24BA64738175E87EE00B3D7FD3`。
- 真实非预览客户端仅驻留 `DISPLAY2`；经用户授权在 DAL↔zhang 会话进行验收发送，消息出现且服务端确认后的草稿清空。副屏指针探测确认顶部热区显示上下调整光标，用户确认拖拽工作。最终“贴边窄热区、无灰色把手”的视觉位置由用户自行复验。
- 本轮未用真实发送替代门禁：尚需人手在 Windows Machine 验收物理 Enter/Ctrl+Enter/IME、clean VM 或 Stage 21 门禁。
