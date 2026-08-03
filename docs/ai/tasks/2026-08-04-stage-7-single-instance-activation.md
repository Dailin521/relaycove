# 阶段 7 单实例激活转交与 fail-closed 路由

## 状态

- `in_progress`
- 分支：`agent/stage-7-single-instance-activation`
- 基线：`ff9e50f6ec3fb250c32f99a7234c40be4b90c92f`

## 目标

在现有 Windows App SDK 通知传输上建立唯一进程入口：次实例把完整 Windows 激活参数交给主实例并在确认后退出，主实例把冷启动、运行中通知和重定向激活汇入同一串行去重路由。通知目标只有在当前账户作用域一致且会话仍被本地权威状态允许时才交给导航 sink；旧账户、撤权、未知、fatal 或尚无活动账户全部 fail-closed，不能仅凭 Toast 中的 ID 打开缓存内容。

## 已知事实

- `已验证`：绿色集成头本地/远端均为 `ff9e50f6ec3fb250c32f99a7234c40be4b90c92f`；前序 Windows 通知平台 Full 为 555/555，通知定向集 830/830，安装态 production builder payload/Register/Show/GetAll/Remove、WPF 非阻塞启停、model drift 与八项目漏洞审计通过。
- `已验证`：当前 `App.OnStartup` 总会创建 `MainWindow` 后注册通知；通知 host 对运行中及当前 `AppNotification` 解码后直接调用只恢复窗口的 sink，没有实例键、转交、账户/权限复核或重复激活屏障。
- `已验证`：现有严格 target 已完整携带 `AccountScopeId`，`ClientAccountRuntime.Identity.Id` 是当前账户真源；`AccountScopedLocalCache.GetNotificationConversationAccessStatus` 能同步区分 `Ready`、`UnknownConversation`、`RevokedConversation` 与 `FatalScope`，但 App 尚未组合认证 runtime 或聊天 UI。
- `已验证`：本机 Windows App SDK 2.3.1 双进程临时 apphost 探针已真实验证固定 key 的 `AppInstance.FindOrRegisterForKey` 与 `RedirectActivationToAsync`：主实例收到完整 `Launch` activation，次实例在 redirect await 返回后以 0 退出。官方文档说明 AppInstance 列表按用户与应用版本隔离，WPF STA 不得同步阻塞 redirect async。
- `已验证`：工程方案 13.5/13.7 要求旧账户或已撤权目标不显示缓存、完整目标重定向、重复目标不创建第二窗口或重复导航，并由主实例重新校验账户和当前会话权限。

## 假设

- `假设`：基于已经通过的完整 activation 实机探针，固定 AppInstance key 比另建 `Mutex + Named Pipe` 更小且更符合 Windows App SDK 激活语义；本任务用生产 app 双进程 smoke 固化最终选择。
- `假设`：聊天 UI 尚未存在时，路由只定义“已授权导航命令”sink；生产 App 在没有活动账户上下文时拒绝所有通知目标。阶段 8 组合真实账户 runtime 与聊天导航后复用该门，不在本任务伪造占位聊天内容。

## 范围

- 必须实现：
  - 固定、进程级 AppInstance key；主实例订阅 redirected activation，次实例转交当前完整 activation、等待系统确认后退出，且不创建窗口、不注册通知。
  - 把主实例当前 activation、redirected activation 和 `AppNotificationManager.NotificationInvoked` 归一为串行 dispatcher；严格解析失败只记脱敏诊断。
  - 普通 Launch 只恢复唯一主窗口；通知 Message/Unread target 必须先匹配显式活动 `AccountScopeId`。Message 还必须由当前账户访问检查返回 `Ready`，其他状态全部拒绝。
  - 成功目标按完整判别联合身份幂等；并发或重复来源不得创建第二窗口、重复调用导航 sink，拒绝结果不得预先“吃掉”将来可能合法的目标。
  - 启动、redirect、停止与日志边界有确定结果；AppInstance/WinRT 调用不阻塞 WPF Dispatcher，不记录 activation 原文、账户、会话、消息或异常 message。
  - fake 边界自动化，以及本机 production Client 双进程 smoke，验证第二次启动收到确认后退出且只保留一个主窗口/主进程。
- 允许修改：
  - `src/RelayCove.Client/`、`tests/RelayCove.Client.Tests/`、`docs/ai/`。
- 明确不做：
  - 登录/账户选择 UI、持久认证启动组合、聊天列表/消息定位、未读总览页面或阶段 8 视觉实现。
  - 提示音、`FlashWindowEx`、托盘、关闭隐藏、彻底退出、开机启动、附件、搜索、更新或发布部署。
  - Shared/Server Web 契约、SQLite schema/migration 或新外部依赖。

## 验收标准

- [ ] 没有现有实例时本进程成为唯一主实例并处理当前 activation；已有实例时次实例不创建窗口/通知 host，完整 activation redirect await 成功后退出。
- [ ] 主实例普通 Launch 恢复同一个窗口；有效 Message/Unread target 只在当前账户门通过后各调用导航 sink 一次，冷启动、运行中和 redirect 重复来源不重复导航。
- [ ] 无活动账户、旧 `AccountScopeId`、未知/撤权/fatal 会话、非法 codec、停止期和 redirect 失败均 fail-closed，不显示缓存、不泄漏标识、不形成第二主实例。
- [ ] AppInstance 与路由并发/取消/异常/重复有自动化；WPF Dispatcher 不同步等待 WinRT redirect 或通知原生调用。
- [ ] production Client 双进程 smoke、Fast/Full、关键定向压力、model drift、八项目漏洞审计、空白和独立复核通过。
- [ ] 实际聊天会话定位与未读总览因阶段 8 UI 尚不存在明确保持未验证，但授权导航 sink 的输入与拒绝边界完整可验收。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- production Client 证明 AppInstance 不能可靠转交完整通知 activation，必须改用自定义 IPC 或改变安装/身份模型。
- 当前账户/访问门必须通过新增公共协议、数据库字段或提前实现阶段 8 UI 才能做到 fail-closed。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件；用户已预授权绿色 push/快进集成和普通工程决策，不对已覆盖事项重复询问。

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 完成阶段 7 提示音、任务栏闪烁与托盘生命周期的最小切片。
