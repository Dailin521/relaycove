# 阶段 8：Production 账户与登录恢复壳

## 任务定义

- **任务名称：** 阶段 8 production 账户组合、登录/恢复与最小账户壳
- **状态：** `已完成`
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

- [x] 无凭据首次启动显示登录页；有效凭据自动进入账户壳；损坏、过期、服务不可用与取消均可恢复到明确且可重试的 UI 状态，UI 线程不被网络/数据库阻塞。
- [x] 登录输入校验、重复提交、成功/失败状态均有测试；密码/令牌/账户 scope/服务器 URI 不进入日志、异常文本、`ToString()` 或托盘。
- [x] 同一时刻最多一个认证操作、session、runtime 与 activation lease；创建/start 失败、注销和 App 退出均按所有权安全清理，不残留旧账户授权或跨账户数据。
- [x] 成功 start/retry 的真实 `ConnectionState` 同时更新账户壳和托盘；未读保持显式 `0（未接线）`，不能标记为端到端已验证。
- [x] 窗口可见/最小化/前台变化发布到当前 runtime；授权通知可恢复窗口并进入受控导航入口，当前无会话 UI 时只停在账户壳且不展示伪造缓存内容。
- [x] Fast/Full、关键并发/失败定向重复、model drift、八项目漏洞审计、空白检查、真实 Windows smoke 与独立复核通过。

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

- 最终代码检查点 `93d8fd5883a45ef154ef4612da7e7fcb8b9f6dc7`（账户壳主体 `3aaa93c26263b308ef57d089383bc00e8c8ae7b3`、SQLite 隔离 `1619024036f8094c8f117d658096dcb6a7977c48`）增加 production `ClientAccountComposition`、账户 shell coordinator/presenter 与窄生命周期接口。主实例才创建单一 HttpClient、DPAPI 持久认证和 runtime factory；Restore/Login/Retry/Logout/Dispose 串行拥有至多一个 session、runtime 和授权 lease。
- runtime 只有在权威 Startup Sync 可用时建立 notification activation lease；失败时目标继续停放，成功 Retry 后建立 lease 并异步重放。Startup/Retry 的 `AuthenticationRequired` 先撤 lease，再 logout 清凭据回登录态；窗口 activity 在 runtime 创建前缓冲，创建后重放。
- `App` 接入真实 LocalAppData 组合、desktop attention、账户快照、托盘连接状态、通知注册可见降级与账户优先的异步退出顺序。最小 WPF 登录/账户壳立即清空 PasswordBox，区分恢复/登录/启动/重试/注销状态，并明确显示总未读 `0（尚未接线）` 和下一切片的会话占位。
- Claude #47 challenge 的 `AuthenticationRequired` 僵死、通知降级不可见、授权重放消息泵重入、排队操作与原语 Dispose 竞争、无锁 validation 快照、注销 activity 噪声和 scope `ToString()` 泄漏均由 Codex 复算、修正并补回归；`SignedOut=0` 保持安全默认值，不按风格建议改为可误表示的 1-based 默认。
- 两次独立 Full 在阶段 7/8 均暴露同一既有 Client SQLite 全局 pool 清理竞态；`1619024` 以一个 `DisableParallelization` xUnit collection 覆盖全部 11 个直接使用 SQLite 的 Client 测试类，不改 production、schema 或依赖。
- Claude #49 固定检查点复核提出的 process-exit `HttpClient` 所有权、冷通知注销线程、停机分类、登录长度、无障碍与登出双失败均已复算。`93d8fd5` 让 detach 保留 runtime 依赖、阻塞原生调用离开 UI 线程，并用无敏感信息的 durable clear barrier 阻断文件锁 + 远端 revoke 失败后的跨进程自动恢复；成功新登录先发布凭据再解除 barrier。动态状态只在文本真实变化时抛 `LiveRegionChanged`，`CredentialClearFailed` 与认证失败同时显示。
- 冻结 `DEC-032`；持续连接事件、真实会话/消息导航、总未读、周期 Sync 和真实服务器双客户端仍留在后续切片，不用边界快照或占位文案冒充完成。

### 验证证据

- `已验证`：最终 Fast 通过；Shared 35、Server 175、Client 447、Updater 1，共 658/658，Debug 构建 0 警告、0 错误。最终 Full 的 Release 构建、658/658、format 与空白检查通过。
- `已验证`：账户壳定向状态机/并发/取消/授权/activity/脱敏、凭据文件锁/跨 store 重启/新登录解除 barrier、detach 依赖、后台原生调用和双失败文案回归通过；最终相关定向集 71/71。六条 review-fix 关键回归连续 10 轮 60/60；SQLite 隔离检查点完整 Client 441/441 连续 5 轮 2,205 次，后续 review-fix 检查点完整 Client 446/446 连续 5 轮 2,230 次全部通过，最终新增 presenter 回归由 447 项 Full 覆盖。
- `已验证`：首次 Stage 8 Full 仅在既有 `NotificationRecoveryTests.AuthoritativeSnapshot_AfterRestart_ReemitsUntilPlatformClearIsAcknowledged` 出现一次 `ObjectDisposedException`。根因为其他并行测试 teardown 的进程级 `SqliteConnection.ClearAllPools()` 关闭该测试正在使用的 native handle；11 个 SQLite 测试类进入独占 collection 后，完整 Client 5 轮与 Full 均通过。
- `已验证`：最终 EF Core `has-pending-model-changes` 报告 model 无变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 对 8 个项目均无已知漏洞；`git diff --check` 通过。
- `已验证`：真实 Release WPF 无凭据启动显示登录页和“系统通知：可用”；空提交显示输入格式错误。向不可达本机地址提交时密码只显示掩码并在请求前清空，`SigningIn` 保持禁用登录面板，随后显示服务器暂不可用而不误进账户壳。普通 Close 隐藏到托盘，第二次启动退出并恢复同一窗口；探针后精确终止被测 executable，残留进程 0，LocalAppData 下未产生账户缓存或凭据目录。
- `已验证`：Stage 7 已实机证明同一托盘 host 的 Exit 可彻底退出；本切片以 coordinator/composition 自动化和代码序复核证明账户先于 notification/router/AppInstance 收敛。当前桌面自动化不能直接操作 Windows notification area，因此没有把本轮精确 `Stop-Process` 冒充新的托盘 Exit 实机证据。
- `已验证`：最终 review-fix 后真实 Release WPF 再启动成功，登录页布局与 UI Automation 树完整，系统通知可用；前一轮不可达本机地址 smoke 证明 password 提交前清空、SigningIn 禁用控件且可见 busy 名称、失败后恢复可操作。关闭隐藏后次实例恢复同一 HWND；最终精确终止被测 executable，残留进程 0，Authentication/Accounts 目录均不存在。
- `已验证`：Claude #47 job `199dd547`（17 分 26 秒，`$8.26`）完成 challenge；#48 job `4a674abb`（11 分 24 秒，`$2.67`）确认 11 个 SQLite 类覆盖完整且当前 collection 方案可交付；#49 job `87982d3f`（12 分 47 秒，`$4.44`）给出有条件合并项；#50 job `1aa031c1`、session `1aa031c1-7f38-4d11-95a4-69dc96389b54` 使用 Claude Code 2.1.220、实际 `claude-opus-5`/XHigh，16 分 31 秒、显示费用 `$5.01`，复核 barrier/崩溃顺序/跨 store/新登录、生命周期与无障碍。所有成立项均由 Codex 复算、修正并以最终 Fast/Full/实机 smoke 验证；Narrator 实际播报仍未验证，不能由静态 UIA 事件冒充。
- `未验证`：未读取 M5 VPS 配置，也未用真实服务器凭据做端到端恢复/登录、真实 SignalR 持续状态、双客户端通知点击聊天导航或总未读；认证目录整体只读/ACL deny、Narrator 实际播报、系统注销/关机和隐藏托盘时不可见的任务栏闪烁仍保留明确限制。

### 文件范围

- `src/RelayCove.Client/Accounts/`、`Auth/`、`Storage/AccountScopeIdentity.cs`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
- `tests/RelayCove.Client.Tests/Accounts/ClientAccountShellCoordinatorTests.cs`、scope 脱敏回归及 11 个 SQLite 测试类/共享 collection。
- `docs/ai/DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md` 与本任务记录。

### 决策与限制

- production 只支持当前进程一个账户；旧账户必须先完整终止才能建立新 runtime。凭据目录与账户缓存目录分离，日志、异常和对象格式化不包含路径、服务器、用户、scope、密码或令牌。
- notification registration 的冷启动门仍服从 `DEC-029/030`；普通环境失败只显示“系统通知不可用”并让账户继续工作，不能永久完成尚未派发的候选。
- 账户壳只在 start/retry/logout 边界读取真实 `ConnectionState`；没有持续事件时不宣称实时。总未读固定显式 `0（未接线）`，通知授权只进入受控占位入口，不展示缓存内容。
- SQLite collection 是对进程级 `ClearAllPools()` 的最小测试隔离；以后任何直接或间接打开 Client SQLite 的新测试类都必须加入该 collection。当前 client suite 墙钟约 7 秒，可靠性收益高于已测得的并行度损失。
- clear barrier 只收窄文件级占用导致的删除失败；若整个认证目录不可写且远端 revoke 同时失败，只能返回 `CredentialClearFailed`、记录脱敏类型并要求用户修复目录权限，不能声称旧 token 已绝对清除。

### 下一步

- 接入账户隔离的会话列表读取 facade、持续连接/总未读状态与双栏主窗口会话列表。
