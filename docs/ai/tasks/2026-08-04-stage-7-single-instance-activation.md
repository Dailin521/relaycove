# 阶段 7 单实例激活转交与 fail-closed 路由

## 状态

- `completed`
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

- [x] 没有现有实例时本进程成为唯一主实例并处理当前 activation；已有实例时普通次实例不创建窗口/通知 host，完整 activation redirect await 成功后退出。Windows 冷 COM 进程必须先注册以取得当前参数，若不是主实例则只 detach 后退出，不注销共享身份。
- [x] 主实例普通 Launch 恢复同一个窗口；在显式活动账户 fake 边界中，有效 Message/Unread target 各调用导航 sink 一次，运行中和同进程重复来源不重复导航。跨进程在“旧主已处理但确认前退出”边界保持 at-least-once，不宣称跨进程 exactly-once。
- [x] 无活动账户、旧 `AccountScopeId`、未认证会话、缺权威快照、未知/撤权/fatal 会话、非法 codec、停止期和 redirect 失败均 fail-closed，不显示缓存、不泄漏标识、不形成第二主实例。
- [x] AppInstance 与路由并发/取消/异常/重复有自动化；WPF Dispatcher 不同步等待 WinRT redirect 或通知原生调用；通知注销完成前不释放实例键。
- [x] production Client 双进程/并发/交接/kill/真实 COM smoke、Fast/Full、关键定向压力、model drift、八项目漏洞审计、空白和独立复核通过。
- [x] 实际活动账户生产接线、聊天会话定位与未读总览因阶段 8 UI/认证组合尚不存在而明确保持 `未验证`；本切片验收的是授权导航命令及拒绝边界，不把 fake sink 冒充真实 UI 导航。

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

- `b8589669e6015b884f171456cc5d34fd402e4212` 增加固定 `RelayCove.Client.Primary` AppInstance key、唯一当前激活读取者、64 项早到激活缓冲、原始 `AppActivationArguments` 重定向，以及目标进程退出观察、1 秒重选观察窗和最多三次继任者改向。重定向确认有 10 秒边界；目标在确认前退出时不等满超时而重新选举，仍无法确认则 fail-closed。
- 冷 COM 命令行只认 Windows 实机给出的独立精确 token `----AppNotificationActivated:`；该路径在读取当前激活前注册通知，冷非主实例只 detach。普通次实例不触发通知 manager。当前激活在第一次选举时捕获一次，后续重选复用同一原始参数。
- 当前、redirected 与运行中通知统一进入 WPF 异步 dispatcher。路由只保留一个两分钟 pending 目标；活动账户、认证状态、规范 scope 与内存权威会话快照/撤权状态全部通过后才恢复窗口并调用导航 sink。已授权完整目标以 5 秒/64 项窗口去重；重复点击仍恢复窗口但不重复导航，拒绝或导航失败不消费目标。
- 优雅退出先停止 dispatcher/router，再在后台收敛原生通知注销，最后释放 AppInstance key；异常退出 fallback 也先 detach 通知回调后释放 key，避免旧主注销与继任者注册交叠。Stage 8 仍负责把真实账户 runtime 与聊天/未读 UI 接入现有 lease 和 sink。

### 验证证据

- `已验证`：最终代码提交 `b8589669e6015b884f171456cc5d34fd402e4212`；Client Release 389/389，activation filter 60/60，连续 10 轮压力 600/600。Fast 与 Full 均为 600/600；Debug/Release 构建 0 警告、0 错误，format 与 `git diff --check` 通过。
- `已验证`：最终 production Release 候选的优雅关闭交接连续 30 轮通过，每轮同时启动 10 个竞争进程且最终仅一个可响应窗口/进程；另有 10 次强杀恢复、20 路冷启动、20 个继任者竞争、10 个普通次实例 redirect 等收敛期实机 smoke 通过。
- `已验证`：交接后继任者最小化时直接调用真实 Windows `INotificationActivationCallback` 返回 `HRESULT 0`，仍仅同一最小化可响应进程，证明旧主注销没有拆掉继任者注册。无现有进程时同一真实 COM callback 冷启动恰好一个可响应窗口；实机命令行形状为独立精确 marker token，另带 `-Embedding` 参数。运行中无账户 callback 同样返回成功且保持一个最小化进程，验证 fail-closed 而非假装导航。
- `已验证`：EF `has-pending-model-changes --no-build` 返回无变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 的八个项目均无已知漏洞；没有临时探针目录或进程残留。
- `已验证`：Claude #43 全局 0.5 job 返回 `REVISE`，有效发现已落实为当前读取者所有权、交接/认证/权威快照与去重修正；#44 本机 Claude Code 2.1.220、真实 `claude-opus-5`/XHigh 只读复核对收敛后工作树返回 `REVISE`。其唯一成立代码阻断——通知注销前提前释放实例键——已由退出顺序回归、30 轮交接和交接后真实 COM callback 修正复验；冷 marker 已有实机证据，生产账户/UI 接线按冻结范围保留阶段 8，其余 P2 已补测试、修正一次读取或记为已知边界。
- `未验证`：系统通知中心视觉点击的鼠标自动化、真实已登录账户导航和聊天定位；本切片以 Windows 实际 COM callback 覆盖同一原生激活入口，视觉/UI 体验留给阶段 8/11 Gate。

### 下一步

- 完成阶段 7 提示音、任务栏闪烁与托盘生命周期的最小切片。
