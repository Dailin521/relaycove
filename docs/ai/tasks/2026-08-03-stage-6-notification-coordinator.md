# 阶段 6：通知旧缓存收养、轮次 gate 与平台无关协调器

## 状态

- `completed`
- 分支：`agent/stage-6-notification-coordinator`
- 基线：`47d442223916e0da1651d9ee9669af372c37817d`

## 目标

在不接 Windows Toast/WPF 的前提下，把已提交的逐消息通知候选接入单账户、单串行 `NotificationCoordinator`。升级前缓存必须先以 durable 版本键原子收养，避免首次 Recovery 补弹历史消息；Sync 与 Realtime 必须通过同一原子 round gate 分流，Startup/后台对账只扫描有界 Recovery，平台接受、瞬时失败、永久不可用、撤权与崩溃窗口均保持 `IsNotificationHandled` 唯一真源。

## 已冻结证据

- `已验证`：绿色集成头本地/远端均为 `47d4422`；上一 read-through 切片 Full 为 447/447，协调器 Release 连续 10 轮 310/310，model drift 与八项目漏洞审计通过。
- `已验证`：Realtime 合并已在事务提交后返回单个 `NotificationCandidateMessageId`，Sync 页事务返回候选 ID 集合；两个调用方当前都丢弃它们，尚无平台副作用。
- `已验证`：`LocalMessages.IsNotificationHandled` 已存在且只允许 false→true；`LocalAppState` 可保存收养版本，无需 SQLite schema/migration。旧版本写入的 false 无法证明仍应提醒，直接 Recovery 扫描会造成升级通知洪水。
- `已验证`：现有 `ClientSyncCoordinator` 提供账户 single-flight、固定 SyncReason 和至多一次补跑；`LocalCacheRealtimeEventSink` 是提交后 Realtime 候选的唯一入口。账户 factory 在 Realtime/Sync 启动前拥有独占 cache 初始化窗口。
- `已验证`：规范要求派发前重检静音；但当前 `ConversationDto` 缺少 `IsMuted`，`LocalConversations.IsMuted` 始终保留默认 0，权威列表无法更新它。该加法契约缺口必须在实际派发前收敛，不能假设所有会话永不静音。
- `已验证`：Claude 0.5 #38（job `b72844be-9c07-4dde-83e6-89e5fd7d5e4a`）请求 Opus/XHigh、实际回落 `claude-sonnet-5` 且 `model_mismatch=true`，631550 ms 后 `REVISE`；其平台三态、round `try/finally`、generation、非持久 Recovery cursor 与收养事务建议均由 Codex 复算并落实。#39（job `0fff90fa-d3e1-4784-ba27-642241cbf7a3`）同样实际回落 `claude-sonnet-5` 且 `model_mismatch=true`，2265022 ms 后指出历史 tombstone 每轮重放与取消轮错误丢弃 Realtime-first 两项阻断，均已修正并由本机回归证明；其他建议由 Codex 逐项裁定。

## 范围

- Shared `ConversationDto` 增加向后兼容的 `IsMuted`，Server 创建/详情/完整列表按当前用户成员状态返回，Public 无个人状态行时为 false；Client 权威快照落入本地 `IsMuted`。不新增静音写 API。
- cache 初始化提供一次性 notification-state adoption：在生产者启动前、同一事务把版本键之前的所有 false 置为 handled 并提交版本；失败不写版本、不开放 Recovery。
- Storage 提供仅按显式 ID 读取/复核候选、按显式 ID 标记 handled，以及最多固定批量的 Recovery 快照；撤权、已读、本人、静音、前台会话、DND/系统禁用和 `None` 在决策事务单调收敛。
- 新增平台无关通知请求/结果与适配器接口。只有协调器可调用平台；Accepted 后提交对应 handled，Transient 保持 false，Permanent/配置禁用置 true。平台接受后、本地置位前崩溃按 at-least-once 处理，不宣称 exactly-once。
- 新增同账户 round gate：Sync round 开启后收集 Sync 与并发 Realtime 首次来源；关闭与 Realtime 即时分流原子切换。Startup/后台权威快照后才可构造有界 Recovery；失败轮次把 Realtime 候选恢复即时决策，Sync 候选留给 Recovery。
- 接入 Sync、Realtime、账户 factory/runtime 生命周期；调用者取消只取消等待，终止停止生产者并等待通知协调器，再释放 cache/session。平台和设置由抽象依赖注入，默认未配置平台不得伪装成功。

## 允许修改

- `src/RelayCove.Shared/Conversations/`
- `src/RelayCove.Shared/Messages/`
- `src/RelayCove.Server/Services/ConversationCommandService.cs`
- `src/RelayCove.Server/Services/ConversationQueryService.cs`
- `src/RelayCove.Client/Notifications/`
- `src/RelayCove.Client/Storage/`
- `src/RelayCove.Client/Sync/`
- `src/RelayCove.Client/Accounts/`
- 对应 Shared/Server/Client 测试
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现 Windows App SDK/Toolkit Toast、声音、`FlashWindowEx`、任务栏/托盘、WPF 可见性事件、激活 Tag/Group/IPC 或真实通知权限探针。
- 不实现会话静音写 API、全局免打扰设置 UI、主动打开会话批量已读、History/SendResponse HTTP、附件/搜索或多账户 UI。
- 不增加 SQLite schema/migration、依赖、服务端通知服务、outbox、消息队列、后台常驻新进程或严格 exactly-once。
- 不让 Realtime kick 全表扫描，不让前台 `WindowActivated` 消费旧 Recovery，不在本地事务提交前产生任何外部副作用。

## 验收标准

- [x] 第一次初始化原子收养全部旧 false 并写 durable 版本；崩溃/失败回滚，重启幂等，新版本候选不被再次收养。
- [x] 权威 `IsMuted` 通过加法 Web JSON/DTO/Server 投影进入本地快照；Public 默认 false，其他会话按当前成员状态，既有协议字段不变。
- [x] 唯一串行协调器派发前重检显式 ID；Accepted/Transient/Permanent/None、部分成功、Summary 原子确认、撤权与日志脱敏均有真实 SQLite + fake platform 回归。
- [x] round gate 在 Sync/Realtime/关闭竞争中无丢失、无双派发；完整/失败/取消轮次按首次来源分流，Startup/WindowActivated/前后台 Reconnect/Periodic 策略与规范一致。
- [x] Recovery 只在允许的权威对账后扫描、固定上限且不饿死后续候选；进程重启与接受后崩溃窗口明确验证为 at-least-once。
- [x] runtime 创建/启动/停止、调用方取消和账户隔离通过；无平台实现时不调用外部 API、不把 transient 假成功。
- [x] 定向测试、关键竞态压力、Fast/Full、model drift、八项目漏洞审计、白名单、空白与独立复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~Notification|FullyQualifiedName~ClientSync|FullyQualifiedName~ClientAccount" --no-restore
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --filter "FullyQualifiedName~Conversation" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 必须引入新的持久表/迁移、Windows 包依赖或改变消息/Sync/认证协议，而不是加法补齐既有 `IsMuted` 投影。
- 无法在不持有 SQLite gate 跨平台调用的前提下，让撤权与在途通知至少可靠收敛清除。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- Shared `ConversationDto` 增加缺省为 false 的加法 `IsMuted`，Server 创建/查询按当前成员投影，Client 权威快照写入本地并在消息事务内直接抑制静音候选；legacy JSON 缺字段回归通过。
- cache 以 `NotificationStateVersion=1` 原子收养旧 false，提供显式 ID 复核/确认和最多 200 条、无持久 cursor 的 Recovery；损坏候选单行隔离，Summary 确认单事务，平台接受后崩溃窗口保持 at-least-once。
- 新增平台三态、串行 `ClientNotificationCoordinator` 与 generation round gate，接入 Sync、Realtime、read-through 和账户 runtime；所有撤权来源都在 deny/tombstone 后清平台状态，终止在 cache 前等待协调器。
- 平台暂不可用只保留仍合格候选的 false；初次及 PerMessage/Summary 二次复核仍把已读、静音或当前前台等明确抑制项置为 handled，避免平台恢复后补弹迟到通知；10/11 自动策略边界有精确回归。
- 平台会话清理新增 durable pending/completed 确认：未确认清理会跨快照、重启和重新加入重试，已确认 tombstone 不再永久重放；失败或取消同步轮次均释放 Realtime-first 候选。
- 代码检查点：`d5f71e7965d8df36e7b54a47f2ee29e4b98e9240`；撤权/生命周期加固检查点：`b645f0cdd46232ed7feccfbf1cb8b1360ae77c4d`；策略边界检查点：`6b6c36d29835a1d6420646aac6cfd4a126dbd69b`；最终代码检查点：`92b924f2ee3dd45e25ee0c9ff3358b346b38f3a8`；架构决定记录为 `DEC-028`。

### 验证证据

- `已验证`：`pwsh ./scripts/verify.ps1 -Mode Fast` 与最终 `-Mode Full` 通过；最终 Release 构建 0 警告/0 错误，Shared 35、Server 175、Client 282、Updater 1，共 493 项测试通过，format 与 `git diff --check` 通过。
- `已验证`：真实 SQLite 覆盖旧缓存收养回滚/幂等、200/201 Recovery 翻页与迟到低 ID、重启、accepted-before-mark、损坏 payload 隔离、Summary 回滚、静音事务、持久 tombstone 清除重放；fake platform 覆盖 Accepted/Transient/Permanent/Unavailable/Disabled/DND/None、部分成功、10/11 阈值、调用方取消、Dispose、撤权和日志脱敏。
- `已验证`：round gate 100 次 Realtime/Close 竞争每个 ID 恰好一次，generation 旧 token 隔离，Startup/WindowActivated/前后台 Reconnect/Periodic、失败/取消策略通过；最终通知/撤权 Release 定向集连续 10 轮 460/460。
- `已验证`：`dotnet ef migrations has-pending-model-changes ... --no-build` 返回无模型变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 八个项目均无已知漏洞；改动白名单与空白检查通过。
- `已验证`：Claude #38 初始 challenge 的有效建议已复算；#39 的两项阻断均在 `92b924f` 收敛，平台清理挂起/终止的真实 Windows liveness 建议明确留给阶段 7 实现时解决。真实 Windows Toast 行为未在本切片验证，按非目标留给阶段 7 安装态测试。

### 下一步

- 进入阶段 7，先以官方 Windows 资料和本机探针对比 unpackaged WPF 可用通知/激活路径，固定权限探针、稳定 Tag/Group 与单实例转交方案，再接声音/闪烁、托盘和 WPF activity；真实 VPS/双客户端仍留到 M5 Gate。
