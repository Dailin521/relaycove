# 阶段 7 Windows 通知平台、稳定身份与运行时门禁

## 状态

- `completed`
- 分支：`agent/stage-7-windows-notification-platform`
- 基线：`7cf908eb127ddf8551853d3ce9897c90b554e78c`

## 目标

把阶段 6 的平台无关协调器接到可真实运行的 unpackaged WPF Windows App SDK 通知传输：平台调用只接受显式账户作用域，Toast 使用规范固定的 Tag/Group 与严格激活目标，应用按官方顺序注册/注销通知，并在平台禁用、缺运行时、异常和终止时返回可验证的三态结果。该切片只完成原生通知提交/移除和激活目标入口，不伪造尚未存在的聊天导航或托盘 UI。

## 已知事实

- `已验证`：基线 `7cf908e` 已含 `DEC-028` 的单串行协调器、Recovery、generation round gate 与 durable 会话清理确认；当前 `IClientNotificationPlatform` 的 request/clear 尚未携带 `AccountScopeId`，共享平台无法按规范计算账户隔离 Group。
- `已验证`：当前 WPF `App.xaml` 使用 `StartupUri`，未注册 Windows 通知；Client 为 `net10.0-windows` unpackaged WinExe，未引用 Windows App SDK，默认 factory 仍使用 deferred 平台和 `Unavailable` 设置。
- `已验证`：Microsoft 官方 WPF 文档要求先订阅 `NotificationInvoked`、再 `Register()`，unpackaged 注册无需手工 AUMID/COM，退出时 `Unregister()`；通知不支持提升权限进程。官方现有项目文档要求 `WindowsPackageType=None` 自动初始化，并要求目标机安装与 NuGet 通道匹配的 Windows App Runtime。
- `已验证`：2026-08-03 官方稳定 `Microsoft.WindowsAppSDK`/runtime 为 `2.3.1`。本机 Windows 10.0.26200 x64 安装官方签名 x64 runtime 后，临时探针已验证 `IsSupported=True`、Setting=Enabled、Show/GetAll/RemoveByTagAndGroup 成功；未安装 Main/Singleton 前真实出现 `0x80040154`，不能把缺运行时伪装成功。
- `已验证`：同一临时 apphost 双进程探针已验证 `AppInstance.FindOrRegisterForKey` 与 `RedirectActivationToAsync` 能把 Launch 激活转交给主实例；官方文档说明 WPF STA 不能同步阻塞 async redirect，且版本/用户维护独立实例列表。
- `已验证`：最终代码检查点 `bb4ae92dbdc1332ecc7283619b78567b44a62f04` 已实现 Windows 原生传输、稳定身份、严格 codec、WPF 注册/当前激活读取/注销、注册就绪门和有界原生调用；`07295ff` 后的固定差异修正还覆盖设置探针超时缓存、缺通知 COM 的惰性 manager、注册超时未决重试竞争、真实 Summary 清理、WPF UI 非阻塞生命周期、提交不确定窗口的同 Group 清理屏障，以及迟到清理失败后的精确自恢复。本任务仍未实现单实例 redirect 或目标授权导航。
- `已验证`：Claude #40（job `b8d105e8-48c5-434d-9ebe-34f4de55f450`）请求 Opus/XHigh、实际回落 `claude-sonnet-5` 且 `model_mismatch=true`。其缺 runtime 不应永久烧掉候选、Summary 必须聚合且撤权时清理、同步 Show/设置探针必须隔离和有界等发现经 Codex 复算成立并已实现；把单实例与授权导航作为下一切片硬边界。
- `已验证`：Claude #41（job `2c9aff25-7dd5-4919-bbdb-fc15af41f5cf`）请求 Opus/XHigh、实际回落 `claude-sonnet-5` 且 `model_mismatch=true`，返回 `FIX_REQUIRED`。Codex 复算确认原生 launch 分隔符、注册状态、同步移除隔离和不确定提交/撤权竞争等发现，并在 `5c244b0` 修正；`IsSupported=false` 保留 durable 候选和非 Enabled setting 的配置性禁用仍按既有策略；framework/runtime 自动 bootstrap 的干净机体验明确留到阶段 11。
- `已验证`：Claude #42（job `5ed116ad-1250-4422-84bc-30c939da40a6`）请求 Opus/XHigh、终态实际回落 `claude-sonnet-5` 且 `model_mismatch=true`；确认 #41 的五个核验点成立且无 P0/P1。其剩余 P2 指出迟到精确清理失败会令全局提交电路只能靠同会话权威删除恢复，P3 指出已完成 registration 可令 `ExecuteSynchronously` 在持锁线程内执行无界注销；Codex 复算成立，并在 `bb4ae92` 增加下一提交前精确有界恢复、挂起移除单 flight 和迟到注销独立 LongRunning 收敛，配套回归与本机门禁通过。
- `已验证`：官方资料（访问 2026-08-03）：
  - <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet?pivots=wpf>
  - <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/manage-app-notifications>
  - <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project>
  - <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing>

## 假设

- `已验证`：本切片以 Windows App SDK 2.3.1 framework-dependent unpackaged 模式落地，官方最低系统为 Windows 10 1809；项目同时声明 `TargetPlatformMinVersion` 与 `SupportedOSPlatformVersion=10.0.17763.0`。安装器 runtime 部署与干净机器探针留到阶段 11，当前明确记录为未验证。
- `假设`：`AppInstance` 相比 `Mutex + Named Pipe` 更符合完整 Windows 激活参数转交；本切片只在注册后读取当前通知激活，不注册实例键或重定向，最终单实例选择在下一切片固化。

## 范围

- 必须实现：
  - `Microsoft.WindowsAppSDK 2.3.1` unpackaged WPF 配置、官方顺序的进程级通知注册/注销，以及注册失败的明确诊断边界。
  - `AccountScopeId` 显式贯穿平台 request 和会话清理；按 13.5 精确生成 Base64UrlNoPadding SHA256 Group 与十进制/固定 Tag。
  - 严格、版本化的 `MessageTarget` / `UnreadOverviewTarget` 编码与解析；拒绝重复、未知、超长、空作用域、非法 GUID/消息 ID，日志不含正文、显示名、服务器或账户标识。
  - 真实 Windows 平台构建 PerMessage/Summary Toast，提交前复核 IsSupported/Setting，按会话 Group 与账户 Summary 精确清理；分类 Accepted/Transient/PermanentlyUnavailable，调用方取消和终止不能让协调器无限等待。
  - 默认账户 runtime factory 使用已注册的真实平台与动态 Windows 设置；测试仍可显式注入 fake/deferred 平台。
  - fake/native 边界自动化与本机安装态 smoke：注册、权限、提交、可查询、按 Group 移除；记录环境、步骤、预期和实际。
- 允许修改：
  - `src/RelayCove.Client/`、`tests/RelayCove.Client.Tests/`、Client 项目配置/锁定依赖、`docs/ai/`。
- 明确不做：
  - `AppInstance` 实例键/重定向的单实例正式接线、激活后的账户/权限复核与聊天导航。
  - 提示音、`FlashWindowEx`、托盘、关闭隐藏、彻底退出、开机启动和聊天 UI。
  - 服务端、Shared Web 契约、SQLite schema/migration、VPS 或发布部署。

## 验收标准

- [x] PerMessage/Summary 的 Group、Tag 与判别联合激活参数逐字节符合 13.5；任意账户/会话变化会隔离，解析器对恶意/模糊输入 fail-closed。
- [x] Windows Enabled 时 Show 被接受才返回 Accepted；系统明确禁用为永久配置性抑制，不支持/缺 runtime/未知 COM 为可恢复 transient；任何路径不泄漏消息或身份字段。
- [x] 会话清理仅删除当前 `AccountScopeId + ConversationId` Group，并删除当前账户 Summary；两者成功/永久不存在才允许阶段 6 durable acknowledgement，任一瞬态/取消都保留 pending。
- [x] WPF 先挂 handler 再 Register，随后读取当前通知激活，退出 Unregister；默认 factory 使用真实平台/动态 Setting，测试注入和账户隔离不回归。
- [x] 平台调用的取消/超时/终止边界有确定回归，同步 adapter 在专用线程执行且账户 runtime 不因 Show 挂起无限等待。
- [x] Fast/Full、定向压力、八项目漏洞审计、依赖版本/运行时探针、空白和独立复核通过。
- [x] 本机非提升权限安装态 smoke 真实显示、查询并移除测试 Toast；真实点击授权导航、单实例、托盘和声音如实保持未验证并转下一切片。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
dotnet list src/RelayCove.Client/RelayCove.Client.csproj package
```

## 停止并询问

- Windows App SDK 2.3.1 无法在目标最低 Windows 版本运行，或 framework-dependent 需要改变阶段 11 发布边界。
- 真实平台只能通过提升权限、关闭系统安全设置或提交证书/密钥验证。
- 激活传输被证明必须在本切片同步引入自定义 IPC 才能提交 Toast。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件；用户已预授权绿色 push/快进集成和普通工程决策，不对已覆盖事项重复询问。

## 任务结果

### 修改摘要

- Client 引用 Windows App SDK 2.3.1 和 Debug logger，固定 unpackaged/最低系统属性；默认账户 factory 使用惰性共享真实 Windows 平台和 TTL 动态设置快照，缺 runtime 不会在 factory 构造时提前触发原生 singleton。
- 新增规范 AccountScope/Group/Tag、版本化激活 codec、原生通知 envelope/manager/platform/host；WPF 按 handler→Register→当前 activation 的顺序启动并在退出时 Unregister。
- 同步 `Show/Register/Unregister/Remove` 使用专用 LongRunning 线程和有界等待，WPF 启动与显式退出在后台等待而不阻塞 Dispatcher；注册超时未决时禁止重试直到独立迟到注销收敛。Show 结果不确定时关闭提交并在迟到成功后精确删除，同 Group 撤权在清理收敛前保持 transient pending；精确清理终态失败由下一提交前有界重试，挂起原生移除保持单 flight，不累积线程。撤权同时清会话 Group 与账户 Summary，缺通知 COM/不支持保持可恢复 pending。
- 新增 fake 原生边界、严格解析、设置缓存、挂起/取消/迟到清理、cold activation、稳定身份与安装态真实 Windows smoke 测试；真实 production builder payload 验证 Windows App SDK 用分号分隔 launch 参数并能被严格 codec 往返解析，现有 coordinator/runtime 测试适配显式账户作用域。

### 验证证据

- `已验证`：最终代码检查点 Fast 与 Full 通过；Release 构建 0 警告、0 错误，Shared 35、Server 175、Client 344、Updater 1，共 555 项测试通过，format 与 `git diff --check` 通过。
- `已验证`：通知/协调器定向集 83 项 Release 连续 10 轮，830/830；新增 host/platform 两类修复回归 32 项另连续 10 轮，320/320；此前偶发失败的 `ClientSync_WhenPagesCommit_RequestsReadThroughAfterEveryCursorAdvance` 隔离连续 10 轮，10/10。
- `已验证`：显式 `RELAYCOVE_WINDOWS_NOTIFICATION_SMOKE=1` 安装态测试真实完成 Register、PerMessage/Summary Show、GetAll、会话 Group 与精确 Summary Remove 及最终清理；Debug WPF app 先创建可响应的 `RelayCove` 主窗口，在后台注册，接受关闭、后台 Unregister 并以 0 退出。
- `已验证`：临时外部探针在 runtime Main/Singleton 安装前真实得到 `0x80040154`；安装 Microsoft 官方签名 x64 runtime 后 `IsSupported=True`、Setting=Enabled、Show/GetAll/Remove 成功。安装器 SHA-256 为 `4011748DDF472B7E856D909FDFB4E9B19C3D23FCD8121039AC91F99D5FFA65DB`，不提交安装文件。
- `已验证`：EF model drift 无变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 八个项目均无已知漏洞；Client 顶级新增依赖精确解析为 `Microsoft.WindowsAppSDK 2.3.1` 与 `Microsoft.Extensions.Logging.Debug 10.0.10`。
- `未验证`：Windows 10 1809 真机、无 Windows App Runtime framework/Singleton/Main 的干净目标机启动体验、安装器部署、真实点击后的账户/权限导航和多实例 redirect；自动 bootstrap 可能在进入托管 `App` 前失败，因此前三项由阶段 11 安装/发布切片验收，后两项由下一激活切片验收。

### 下一步

- 提交任务记录、快进集成并清理任务分支。
- 下一切片接入单实例激活转交，完成旧账户/撤权点击 fail-closed 导航门。
