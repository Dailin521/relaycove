# 阶段 8：Production 账户与登录恢复壳

## 任务定义

- **任务名称：** 阶段 8 production 账户组合、登录/恢复与最小账户壳
- **状态：** `进行中`
- **基准提交：** `de15ef589402050ee1072bd3c7ee6c41e3c07b9c`
- **工作分支：** `agent/stage-8-production-account-shell`
- **相关方案章节：** 9.2–9.4、12.7–12.8、阶段 8；`DEC-022`、`DEC-025`、`DEC-028`、`DEC-030`、`DEC-031`

### 目标

把阶段 6/7 已验证的真实认证、单账户 runtime、通知授权路由、桌面 attention 与托盘接到 production WPF 进程。应用启动时先安全尝试恢复凭据，失败则显示可用登录界面；认证成功后进入最小账户壳，可重试连接、注销并回到登录态。

### 已知事实

- `已验证`：当前 `MainWindow` 仍是空 `Grid`，`App` 只组合单实例、通知、attention 与托盘，不创建 `HttpClient`、`ClientCredentialStore`、`PersistentClientAuthentication` 或 `ClientAccountRuntime`。
- `已验证`：`PersistentClientAuthentication` 已提供串行 `RestoreAsync`/`LoginAsync`，DPAPI 凭据按当前 Windows 用户加密；`ClientAccountRuntimeFactory` 会建立账户隔离缓存、Realtime/Sync/read-through/通知协调，并可注入真实 `IClientNotificationAttention`。
- `已验证`：`ClientAccountRuntime.StartAsync` 固定先尝试 Realtime 再执行 Startup Sync；即使连接失败也返回可诊断状态，权威缓存未就绪时通知授权继续 fail-closed。`LogoutAsync` 会先收敛 runtime，再远端注销、清理凭据并释放认证会话。
- `已验证`：`ClientNotificationActivationRouter.ActivateAccount` 可在账户 runtime 存活期建立唯一授权 lease；当前 production 没有调用方，通知点击只能停放，不能导航。
- `已验证`：托盘 `UpdateStatus` 已可线程安全更新，但 production 始终传入 `0 / Disconnected`；runtime 当前没有持续状态事件，最小壳只能在 start/retry/logout 等明确边界刷新真实连接快照。
- `已验证`：本地缓存没有供 UI 读取会话列表/总未读的 production facade；会话列表、消息列表、发送与持续未读更新必须是后续独立切片。

### 假设

- `假设`：本切片采用一个可单元测试、独占认证 session/runtime/activation lease 的账户 shell coordinator，由 WPF 只负责展示状态和转发用户动作，可在不引入 MVVM 框架或依赖注入容器的前提下形成清晰生命周期。
- `假设`：服务器地址、用户名和密码由登录页显式输入；沿用现有 absolute HTTP(S) URI 校验以保留本机 HTTP 开发能力，生产 HTTPS 要求继续由部署 Gate 验证。
- `假设`：最小账户壳显示当前用户、服务器、连接/同步结果以及 Retry/Logout；空会话区明确标为后续切片，不用占位数据伪造聊天能力。

### 范围

- 必须实现：
  - production 组合根创建并安全释放单个 `HttpClient`、凭据存储、持久认证、账户 runtime factory 与账户 shell coordinator；数据目录固定在当前用户 LocalAppData 下且不写日志。
  - 启动自动恢复；无凭据、失效/损坏凭据或远端失败均回到可操作登录页，并用脱敏、可理解的状态提示区分重试与重新输入。
  - 登录页包含服务器地址、用户名、密码；防重复提交，网络/认证/协议/限流失败不泄漏密码、令牌、用户或服务器详情。
  - 认证成功后只创建并启动一个 runtime，建立通知授权 lease，注入真实 desktop attention，发布窗口 activity，并把 start/retry/logout 边界的真实连接状态写入窗口和托盘。
  - Retry 触发 realtime reconnect + sync；Logout 按 lease → runtime/logout → UI/托盘复位顺序收敛并返回登录页；应用退出安全释放仍存活的账户对象。
  - 为状态机、并发/取消、失败清理、重复动作、授权 lease、activity、托盘状态与脱敏日志补自动化；执行真实 WPF 登录页/恢复失败/退出 smoke。
- 允许修改：
  - `src/RelayCove.Client/Accounts/`、`Auth/`、`Desktop/`、`App.xaml.cs`、`MainWindow.xaml(.cs)` 及 Client 项目配置。
  - `tests/RelayCove.Client.Tests/` 对应测试、`docs/ai/` 状态/任务/必要决策记录。
- 明确不做：
  - 会话列表、本地总未读查询 facade、消息列表、发送/回复/@/链接/复制/分割线、历史滚动和失败重试 UI。
  - 新服务端 API、Shared 协议、SQLite schema/migration、第三方 MVVM/DI/UI 依赖或视觉资产体系。
  - 把一次 start/retry 快照冒充持续实时连接状态；持续状态与真实未读由下一切片接入。

### 验收标准

- [ ] 无凭据首次启动显示登录页；有效凭据自动进入账户壳；损坏、过期、服务不可用与取消均可恢复到明确且可重试的 UI 状态，UI 线程不被网络/数据库阻塞。
- [ ] 登录输入校验、重复提交、成功/失败状态均有测试；密码/令牌/账户 scope/服务器 URI 不进入日志、异常文本、`ToString()` 或托盘。
- [ ] 同一时刻最多一个认证操作、session、runtime 与 activation lease；创建/start 失败、注销和 App 退出均按所有权安全清理，不残留旧账户授权或跨账户数据。
- [ ] 成功 start/retry 的真实 `ConnectionState` 同时更新账户壳和托盘；未读保持显式 `0（未接线）`，不能标记为端到端已验证。
- [ ] 窗口可见/最小化/前台变化发布到当前 runtime；授权通知可恢复窗口并进入受控导航入口，当前无会话 UI 时只停在账户壳且不展示伪造缓存内容。
- [ ] Fast/Full、关键并发/失败定向重复、model drift、八项目漏洞审计、空白检查、真实 Windows smoke 与独立复核通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AccountShell|FullyQualifiedName~PersistentClientAuthentication|FullyQualifiedName~ClientAccountRuntime"
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --no-build --configuration Release
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要改变现有认证/刷新协议、账户缓存隔离、通知授权 fail-closed 语义、SQLite schema 或引入新大型依赖。
- 实机登录需要未获授权的生产凭据；可以用本机 TestServer/临时本地服务验证时不得读取 VPS 密钥或扩大外部写入。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4/12.7–12.8/阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现 production 账户组合、登录/恢复与最小账户壳，不提前实现会话/消息 UI。
Codex 负责实现与验收；Claude 仅做持久只读 challenge/review，结论必须由本地代码和测试复算。
运行列出的验证；未运行的项目必须标注“未验证”并说明原因。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- 待完成。

### 文件范围

- 待完成。

### 决策与限制

- 待完成。

### 下一步

- 接入账户隔离的会话列表读取 facade、持续连接/总未读状态与双栏主窗口会话列表。
