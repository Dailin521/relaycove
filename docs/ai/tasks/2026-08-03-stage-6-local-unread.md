# 阶段 6：本地未读与通知候选事务语义

## 状态

- `completed`
- 分支：`agent/stage-6-local-unread`
- 基线：`17329105102dce610f373c8eade813a7d128bb83`

## 目标

在不接 Windows Toast/WPF 的前提下，把规范中的 `IncomingMessageSource`、当前前台会话判定、未读派生与 `IsNotificationHandled` 唯一真源落入现有 AccountScope SQLite、Realtime sink 和 Sync 页事务。首次到达必须在消息提交的同一事务内完成已读/未读、pending read-through、会话预览和通知候选登记；重复/冲突、整页回滚、撤权与账户隔离不得产生重复副作用。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `1732910`；前序单账户 runtime Full 为 382/382、组合 94/94、关键竞态 200/200、model drift 与八项目漏洞审计通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：工程方案 12.6 与 13.1–13.4、`DEC-003` 已冻结四种来源、首次插入副作用、当前前台会话、逐消息 `IsNotificationHandled`、Sync 页/游标原子性和 Round/Recovery 候选边界。
- `已验证`：当前 SQLite 已有 `LocalConversations.UnreadCount/LastReadMessageId/PendingReadThroughMessageId` 与 `LocalMessages.IsRead/IsNotificationHandled`，但 merge/page API 没有来源或活动上下文，插入依赖默认 `false`，也不返回候选 ID。
- `已验证`：权威会话列表先于 Sync 页独立提交；Realtime 可在两者之间到达。任务启动时曾假设可用本地 `LastMessageId` 判断权威覆盖，交错反例已证伪该假设；最终实现改用每次权威登记保存的独立 `AuthoritativeLastMessageId` 内存边界。
- `已验证`：全局 Claude MCP 0.5 持久只读 challenge `3f2699b1-9e8a-4845-b242-c74016360fa3` 与候选 review `7f77374f-6c0d-4872-9912-0dd721930c32` 已完成；Codex 仅采纳经仓库和实测复核的发现，继续作为实现与验收主体。

## 范围

- 在 Shared 落实规范中已冻结的 `IncomingMessageSource` 数值；新增平台无关、可验证的 Client 活动快照，只在“窗口可见、未最小化、有前台焦点且已打开会话”全部成立时解析出前台会话 ID。
- 让 Realtime 与 Sync 写入链把来源和活动快照传入 cache；不从 Storage 读取 WPF/Win32 状态。
- 首次插入按来源、当前账户 sender、有效已读边界和前台会话，在消息/会话同一 SQLite 事务内单调更新 `IsRead`、`IsNotificationHandled`、`UnreadCount`、`LastMessageId` 与必要的 `PendingReadThroughMessageId`。
- `History`、`SendResponse`、本人消息、已读边界内消息和当前前台会话明确不提醒；只有他人未读 `Inserted` 可返回稳定的服务器 message ID 候选。`PendingPromoted`/`Duplicate` 不重复增加未读或候选，但允许 `false -> true` 的观察型抑制。
- 权威会话快照不得覆盖列表获取后由 Realtime 产生的更新；Sync 页对已被快照 `LastMessageId` 覆盖的消息不重复增加未读，对列表之后的新消息补增一次。
- Sync 页的 merge、未读/候选、预览和 `LastSyncCursor` 保持一个事务；冲突/故障整页回滚且不泄漏候选。候选 ID 只作为后续串行协调器的明确输入，不在本切片调用平台 API。

## 允许修改

- `src/RelayCove.Shared/Messages/`
- `src/RelayCove.Client/Storage/`
- `src/RelayCove.Client/Sync/ClientSyncCoordinator.cs`
- `src/RelayCove.Client/Accounts/`
- `tests/RelayCove.Shared.Tests/`
- `tests/RelayCove.Client.Tests/Storage/`
- `tests/RelayCove.Client.Tests/Sync/`
- `tests/RelayCove.Client.Tests/Accounts/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现 NotificationCoordinator、Round/Recovery 内存 gate、NotificationPolicy 选择、Toast、声音、闪烁、托盘、单实例或激活转交。
- 不实现 WPF 窗口事件接线、周期 timer、全局免打扰、会话静音设置 UI、Windows 通知开关探测或平台 API。
- 不实现 History/SendResponse HTTP 编排、主动打开会话的批量已读、服务端 read-through 上报或 pending read-through 重试；本切片只保存规范要求的本地单调目标。
- 不修改服务端协议、消息/会话 DTO、SQLite schema、migration、AccountScopeId、DPAPI、认证/Realtime 连接协议或依赖。
- 不扫描全库 Recovery 候选，不派发或宣称通知 exactly-once。

## 验收标准

- [x] 四种来源值与活动快照判定稳定；无 WPF/Win32 依赖进入 Shared/Storage。
- [x] Realtime/Sync 首次、重复、pending、本人与已读边界/前台路径得到确定且单调的消息、会话、未读和候选结果。
- [x] 权威快照与列表后 Realtime/Sync 交错不丢未读；Sync 整页失败同时回滚消息、候选、预览、未读和游标。
- [x] 当前前台 Realtime 在同事务标记已读/已处理并保存 pending read-through；重复来源不把状态从 true 重置为 false。
- [x] 定向真实 SQLite、Realtime/Sync/runtime 组合、关键竞态、Fast/Full、model drift、八项目漏洞审计、白名单、空白与独立复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~Unread|FullyQualifiedName~SyncPage|FullyQualifiedName~AccountScopedLocalCache|FullyQualifiedName~ClientAccount" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要增加/修改 SQLite schema 或 migration、改变 Shared/服务端 DTO、引入 Windows 平台包，或把通知平台副作用纳入本切片。
- 无法在独立会话快照与 Sync 页之间证明未读不会丢失/重复，或需要新的服务端 snapshot token 才能正确实现。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- Shared 固定四种 `IncomingMessageSource`；Client 增加线程安全、脱敏的窗口活动快照和不可变消息摄取上下文，并由单账户 runtime 同时接入 Realtime sink 与每个 Sync 页。
- `AccountScopedLocalCache` 在既有 scope gate/SQLite 事务内统一处理消息标志、未读、会话预览、pending read-through、权威覆盖边界与稳定候选。本人 pending 只能属于当前账户且创建即已处理；显示名作为可变投影刷新，不再把昵称变化误判为不可变冲突。
- 权威快照使用“服务端残值 + 权威上界之外本地未读行”的互斥区间派生，覆盖列表落后于 Realtime、乱序到达、Sync 回填和前台本地已读。连续已读边界不越过已提交 cursor，原始 pending 目标保留到服务端确认；页内前台 read-through 按会话合并一次。
- outcome 显式返回事务提交后的 ServerMessageId 候选；当前切片不派发平台通知。最终代码检查点为 `c6955cb649d16b8a6d488dd228f99747a8c8c64c`，16 个源/测试路径全部属于任务白名单。

### 验证证据

| 状态 | 证据 |
| --- | --- |
| `已验证` | 最终 `LocalUnreadStateTests` Release 27/27；实现阶段定向 SQLite/Sync/runtime 组合 87/87、Storage + runtime 扩展集 83/83。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` 通过；最终 `-Mode Full` Release 0 警告/0 错误，Shared 34 + Client 203 + Server 175 + Updater 1 = 413/413，format 与 `git diff --check` 通过。 |
| `已验证` | 关键竞态 `MergeIncomingMessage_WhenConcurrentDuplicates`、双页同 cursor、快照 pending 跨 store、并发 runtime Start 共 4 项，Release 连续 10 轮 40/40。 |
| `已验证` | Codex 固定差异自审发现“Realtime 先推进本地预览、History 后首次插入权威上界外消息”会错误吞掉另一条实时未读；改为只让重复未读或权威覆盖内的首次 History 扣减，新增真实 SQLite 回归并连续 10/10。 |
| `已验证` | Claude #35 的 P2 反例经 Codex 复算成立：权威快照推进已读边界后，边界下仍为 `IsRead=false` 的旧行不在当前未读基线内。前台扣减增加 `ServerMessageId > LastReadMessageId` 下界；直接 Sync 页损坏游标改为 fatal rollback，撤权路径同步清理权威边界。与 History 回归合并连续 10 轮 30/30。 |
| `已验证` | 真实 SQLite 10,000 条既有历史 + 200 条前台 Sync 页测试整体 195 ms，通过候选为空、10,200 行单调已读/已处理和未读归零断言；避免逐条全量重写。 |
| `已验证` | `dotnet ef migrations has-pending-model-changes ... --no-build` 返回模型无变化；八个项目 `--vulnerable --include-transitive` 均无已知漏洞。 |
| `已验证` | 全局 Claude 0.5 #33–#35 的有效发现已由 Codex 复算、修正并本机复验；三次请求 Opus/XHigh 均实际回落 `claude-sonnet-5`、`model_mismatch=true`，不冒充目标模型。#35 job `d74f75d8-b3cf-4a16-8985-2467f2801b5d` 耗时 995274 ms、费用 `$4.279979`，其 P2/P3 已在最终代码检查点闭环。 |

### 限制

- 当前没有 NotificationCoordinator、Round/Recovery gate、Recovery 扫描、Toast 或 WPF 事件；候选以 `IsNotificationHandled=false` 和显式 ID 保留，尚未产生外部副作用。
- 下个通知切片在首次 Recovery 扫描前必须用 `LocalAppState` durable 版本键收养旧缓存并把既有历史标记为已处理，避免升级后补发历史 Toast。
- `PendingReadThroughMessageId` 是未裁剪的本地目标。未来 uploader 每次只能提交 `MIN(pending, committed LastSyncCursor)`，并保留 pending 到服务端权威边界确认；本切片不实现 HTTP 上报。

### 下一步

- 实现旧缓存收养、串行通知候选轮次 gate、有界 Recovery 扫描与平台无关 NotificationCoordinator；同时实现按已提交 cursor 钳制的 read-through uploader，再接 Windows Toast 探针。
