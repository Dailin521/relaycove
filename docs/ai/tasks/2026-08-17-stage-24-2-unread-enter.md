# Stage 24.2 — 当前可见消息标读与 Composer Enter

- Status: Active local implementation; narrow tests and Fast passed; independent reviews found no P0/P1
- Branch/worktree: `codex/fix-native-unread-enter` under `E:\WorkSpace\RelayCove-UnreadEnter`
- Starting point: `origin/main@67fcab46cdd87d5a77eec98dc3970a7333d7c8a2`
- External effects: none; no credentials, Realm, Live, push, merge or deployment

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
