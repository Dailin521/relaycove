# 阶段 6：本地未读与通知候选事务语义

## 状态

- `in_progress`
- 分支：`agent/stage-6-local-unread`
- 基线：`17329105102dce610f373c8eade813a7d128bb83`

## 目标

在不接 Windows Toast/WPF 的前提下，把规范中的 `IncomingMessageSource`、当前前台会话判定、未读派生与 `IsNotificationHandled` 唯一真源落入现有 AccountScope SQLite、Realtime sink 和 Sync 页事务。首次到达必须在消息提交的同一事务内完成已读/未读、pending read-through、会话预览和通知候选登记；重复/冲突、整页回滚、撤权与账户隔离不得产生重复副作用。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `1732910`；前序单账户 runtime Full 为 382/382、组合 94/94、关键竞态 200/200、model drift 与八项目漏洞审计通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：工程方案 12.6 与 13.1–13.4、`DEC-003` 已冻结四种来源、首次插入副作用、当前前台会话、逐消息 `IsNotificationHandled`、Sync 页/游标原子性和 Round/Recovery 候选边界。
- `已验证`：当前 SQLite 已有 `LocalConversations.UnreadCount/LastReadMessageId/PendingReadThroughMessageId` 与 `LocalMessages.IsRead/IsNotificationHandled`，但 merge/page API 没有来源或活动上下文，插入依赖默认 `false`，也不返回候选 ID。
- `已验证`：权威会话列表先于 Sync 页独立提交；Realtime 可在两者之间到达。`LastMessageId` 可作为“权威列表是否已覆盖该消息”的本地边界，必须防止快照覆盖列表之后的本地新到达。
- `已验证`：全局 Claude MCP 0.5 持久只读 challenge `3f2699b1-9e8a-4845-b242-c74016360fa3` 已启动；Codex 继续以仓库与实测为主，不等待第二意见才推进。

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

- [ ] 四种来源值与活动快照判定稳定；无 WPF/Win32 依赖进入 Shared/Storage。
- [ ] Realtime/Sync 首次、重复、pending、本人与已读边界/前台路径得到确定且单调的消息、会话、未读和候选结果。
- [ ] 权威快照与列表后 Realtime/Sync 交错不丢未读；Sync 整页失败同时回滚消息、候选、预览、未读和游标。
- [ ] 当前前台 Realtime 在同事务标记已读/已处理并保存 pending read-through；重复来源不把状态从 true 重置为 false。
- [ ] 定向真实 SQLite、Realtime/Sync/runtime 组合、关键竞态、Fast/Full、model drift、八项目漏洞审计、白名单、空白与独立复核通过。

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

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 实现串行通知候选轮次 gate、Recovery 扫描边界与平台无关 NotificationCoordinator，再接 Windows Toast 探针。
