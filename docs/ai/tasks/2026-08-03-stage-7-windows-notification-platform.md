# 阶段 7 Windows 通知平台、稳定身份与运行时门禁

## 状态

- `in_progress`
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
- `已验证`：官方资料（访问 2026-08-03）：
  - <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet?pivots=wpf>
  - <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/manage-app-notifications>
  - <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project>
  - <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing>

## 假设

- `假设`：本切片以 Windows App SDK 2.3.1 framework-dependent unpackaged 模式落地；安装器随包部署与自包含取舍留到阶段 11，但缺 runtime 必须得到明确不可用结果和诊断。
- `假设`：`AppInstance` 相比 `Mutex + Named Pipe` 更符合完整 Windows 激活参数转交，最终选择在下一激活/单实例切片结合 Claude challenge 与仓库实现证据固化；本切片不提前声明单实例已完成。

## 范围

- 必须实现：
  - `Microsoft.WindowsAppSDK 2.3.1` unpackaged WPF 配置、官方顺序的进程级通知注册/注销，以及注册失败的明确诊断边界。
  - `AccountScopeId` 显式贯穿平台 request 和会话清理；按 13.5 精确生成 Base64UrlNoPadding SHA256 Group 与十进制/固定 Tag。
  - 严格、版本化的 `MessageTarget` / `UnreadOverviewTarget` 编码与解析；拒绝重复、未知、超长、空作用域、非法 GUID/消息 ID，日志不含正文、显示名、服务器或账户标识。
  - 真实 Windows 平台构建 PerMessage/Summary Toast，提交前复核 IsSupported/Setting，按会话 Group 清理；分类 Accepted/Transient/PermanentlyUnavailable，调用方取消和终止不能让协调器无限等待。
  - 默认账户 runtime factory 使用已注册的真实平台与动态 Windows 设置；测试仍可显式注入 fake/deferred 平台。
  - fake/native 边界自动化与本机安装态 smoke：注册、权限、提交、可查询、按 Group 移除；记录环境、步骤、预期和实际。
- 允许修改：
  - `src/RelayCove.Client/`、`tests/RelayCove.Client.Tests/`、Client 项目配置/锁定依赖、`docs/ai/`。
- 明确不做：
  - 单实例与 `AppInstance` 正式接线、激活后的账户/权限复核与聊天导航。
  - 提示音、`FlashWindowEx`、托盘、关闭隐藏、彻底退出、开机启动和聊天 UI。
  - 服务端、Shared Web 契约、SQLite schema/migration、VPS 或发布部署。

## 验收标准

- [ ] PerMessage/Summary 的 Group、Tag 与判别联合激活参数逐字节符合 13.5；任意账户/会话变化会隔离，解析器对恶意/模糊输入 fail-closed。
- [ ] Windows Enabled 时 Show 被接受才返回 Accepted；系统禁用/不支持/缺 runtime 明确永久不可用，未知 COM/平台失败为 transient；任何路径不泄漏消息或身份字段。
- [ ] 会话清理仅删除当前 `AccountScopeId + ConversationId` Group，成功/永久不存在才允许阶段 6 durable acknowledgement；瞬态/取消保留 pending。
- [ ] WPF 先挂 handler 再 Register，退出 Unregister；默认 factory 使用真实平台/动态 Setting，测试注入和账户隔离不回归。
- [ ] 平台调用的取消/超时/终止边界有确定回归，真实 adapter 不让账户 runtime Dispose 无限等待。
- [ ] Fast/Full、定向压力、八项目漏洞审计、依赖版本/运行时探针、空白和独立复核通过。
- [ ] 本机非提升权限安装态 smoke 真实显示、查询并移除测试 Toast；真实点击导航、单实例、托盘和声音如实保持未验证并转下一切片。

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

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 选择并接入单实例激活转交，完成旧账户/撤权点击 fail-closed 导航门。
