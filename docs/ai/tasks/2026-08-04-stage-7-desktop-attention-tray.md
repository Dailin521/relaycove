# 阶段 7 提示音、任务栏闪烁与托盘生命周期

## 状态

- `in_progress`
- 分支：`agent/stage-7-desktop-attention-tray`
- 基线：`c94d9a247557e022ea23556ae34d96b5cb1a185e`

## 目标

在已验证的 Toast `Accepted` 边界之后增加进程级桌面提醒，并把 WPF 主窗口改为可长期驻留的托盘生命周期：一次通知 dispatch 即使接受多条 Toast 也最多尝试一次提示音和一次任务栏闪烁；窗口关闭默认隐藏且保留单实例、通知与未来账户 runtime，托盘可恢复唯一窗口并执行彻底退出。所有 Windows 副作用失败只能记录脱敏诊断，不能回退已成功 Toast、重发 Toast 或阻塞账户协调器。

## 已知事实

- `已验证`：绿色集成头本地/远端均为 `c94d9a247557e022ea23556ae34d96b5cb1a185e`；前序单实例切片 Fast/Full 为 600/600，Client 389/389，activation 压力 600/600，真实关闭交接、冷/运行中/交接后 COM callback、model drift 与八项目漏洞审计通过。
- `已验证`：`ClientNotificationCoordinator` 是 Toast 平台调用与 `Accepted` 结果的唯一串行真源；PerMessage 一次 dispatch 可接受多条 Toast，Summary 一次只提交一条。把声音/闪烁放进 `WindowsClientNotificationPlatform` 会按 Toast 重复触发，违反 13.4 的“每轮最多一次”。
- `已验证`：当前 `MainWindow` 是空 WPF Window，`App` 设置 `OnExplicitShutdown`，窗口 `Closed` 立即执行通知注销后释放 AppInstance key；没有 Closing-hide、托盘、提示音或 FlashWindowEx。
- `已验证`：Client 未启用 Windows Forms、没有图标资源；Windows Forms `NotifyIcon` 可由现有 Windows Desktop SDK 提供，不需要 NuGet。第一版可使用系统 Application icon，品牌资源留给视觉资产切片。
- `已验证`：当前 production App 尚未创建认证账户 runtime 或聊天 UI，因此真实总未读、实时连接状态、打开会话停止闪烁都没有生产数据源；本任务只能冻结线程安全更新入口并验证无账户时 `0 / Disconnected`，阶段 8 必须接真实 runtime/cache 状态。

## 假设

- `假设`：进程级 notification attention 接口由一次 `DispatchAsync` 在首次 `Accepted` 后最多调用一次，最小且可证明满足 Realtime 单条与同步轮次批量口径；即使后续本地确认失败，已经成功显示的 Toast 仍应有一次桌面提醒。
- `假设`：使用 `MessageBeep` 触发当前 Windows 用户的系统提示音、使用 `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)` 闪烁到窗口前台，并在 WPF `Activated` 或未来会话打开入口调用 STOP，能在无媒体依赖下满足第一版。
- `假设`：托盘 tooltip/禁用状态项显示有界未读数与连接状态，双击/Open 恢复唯一窗口，Exit 设置显式退出意图后走现有关闭收敛，是当前空壳 UI 下最小可验收形态。

## 范围

- 必须实现：
  - 平台无关、进程级 notification attention seam；只有 Toast `Accepted` 后触发，同一 `DispatchAsync` 最多一次，异常不得改变 dispatch outcome 或候选 handled 状态。
  - Windows `MessageBeep` 与 `FlashWindowEx` 适配器；窗口不在前台时才启动闪烁，窗口激活时停止；对未来“打开对应会话”暴露同一停止入口。
  - Windows Forms `NotifyIcon` 托盘 host，不新增 NuGet；显示图标、有界总未读和连接状态，提供 Open 与 Exit，双击等价 Open，所有回调切回 WPF Dispatcher。
  - 主窗口普通关闭只隐藏；隐藏后普通再次启动/Launch 恢复同一个窗口。显式 Exit 才关闭窗口、释放托盘，并复用通知注销完成后释放 AppInstance key 的安全退出顺序。
  - 状态格式化、并发/重复/异常、窗口与托盘生命周期 fake 自动化，以及本机真实 NotifyIcon/关闭隐藏/再次启动恢复/彻底退出 smoke。
- 允许修改：
  - `src/RelayCove.Client/`、`tests/RelayCove.Client.Tests/`、`docs/ai/`。
- 明确不做：
  - 登录/账户选择、production 账户 runtime 创建、聊天/未读总览 UI、真实总未读和连接状态接线；这些属于阶段 8。
  - 自定义品牌图标/安装器图标、通知中心视觉点击、每账户声音设置、音频文件、音量控制或 DND 新协议。
  - 开机启动、设置页、附件、搜索、更新、发布部署，或 Shared/Server/SQLite schema/migration 改动。

## 验收标准

- [ ] 0 个 Accepted 不触发桌面 attention；一个 dispatch 接受 1..N 条 Toast 只触发一次，且只在首个 Accepted 之后。声音/闪烁异常不改变 Toast 结果、不重试 Toast，后续 dispatch 仍可提醒。
- [ ] 窗口在前台不闪烁；非前台且有 Accepted 时调用正确窗口句柄与 FlashWindowEx flags；窗口 Activated/显式 stop 后停止。无窗口/句柄、P/Invoke 返回失败或异常只记类型/状态，不泄漏通知内容。
- [ ] 托盘 host 始终有图标；状态文本对 `0..int.MaxValue` 未读和所有 `ConnectionState` 有界、安全，Open/双击只恢复唯一窗口，重复 Exit 只发起一次彻底退出。
- [ ] 普通 Close 只 Hide，进程、通知 host 与 AppInstance key 保持；隐藏后第二次启动把同一窗口恢复。显式 Exit 才关闭并按 tray → notification → AppInstance key 的顺序收敛且无进程残留。
- [ ] Fast/Full、关键定向压力、真实 Windows smoke、model drift、八项目漏洞审计、空白和独立复核通过。
- [ ] production 真账户未读/连接更新与会话打开停止闪烁明确保持 `未验证`，不得把默认 `0 / Disconnected` 或 fake 更新冒充端到端账户体验。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- Windows Forms NotifyIcon 与当前 WPF/Windows App SDK 消息泵无法可靠共存，或彻底退出无法复用既有安全注销顺序而必须改变单实例协议。
- 必须新增外部媒体/托盘依赖、公共协议、数据库字段或提前构造假账户/UI 才能满足本任务边界。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件；用户已预授权绿色 push/快进集成和普通工程决策，不对已覆盖事项重复询问。

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 进入阶段 8 账户 runtime 与最小聊天/未读 UI 组合，接入真实托盘状态、通知导航和会话打开停止闪烁。
