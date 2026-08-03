# 阶段 5：SignalR ConversationAccessRevoked 事件

## 状态

- `in_progress`
- 分支：`agent/stage-5-signalr-access-revoked`
- 基线：`82f0a2f5950b5094fd39e78aa113367b83bf0f45`

## 目标

把私有频道成员删除与实时撤权事件形成服务端闭环：只有真实删除在权威写事务提交后，才向被删除用户的全部现有 SignalR 连接尽力发送一次 `ConversationAccessRevoked(Guid conversationId)`；事件故障不改变数据库撤权和既有 204，重复/并发删除不产生重复事件。

## 已冻结证据

- `已验证`：基线 Fast 为 Debug 0 警告、0 错误，Server 171、Shared 29、Client 1、Updater 1，共 202 项通过；集成本地/远端头均为 `82f0a2f`。
- `已验证`：`ConversationCommandService.RemoveMemberAsync` 已在 SQLite 非 deferred Serializable 写事务内重新读取 actor、会话与成员，真实成员存在时删除并提交，不存在时仍按幂等 HTTP 语义返回 204；当前返回值尚不能区分是否真实删除。
- `已验证`：`DEC-003` 固定撤权事件仅为尽力加速，权威 `Complete=true` 会话全集、稳定 403 与 Sync 负责丢失/离线补偿；客户端必须先 deny-set/tombstone 再清理，但客户端实现不属于本切片。
- `已验证`：`DEC-014` 已保证 NewMessage 每次按当前数据库收件人快照而非组投递，所以成员删除提交后开始的新消息发布不会选择旧连接；撤权前已排队帧仍属于后续客户端 deny-set 边界。
- `已验证`：现有 SignalR 用户标识是标准 `sub` GUID，`Clients.User(userId)` 可向同一用户全部连接投递；目标用户即使随后禁用，已存在的连接仍应收到撤权清理信号，因此事件投递不做活跃用户过滤。

## 范围

- Server Hub 契约新增 `ConversationAccessRevoked(Guid conversationId)`。
- 成员删除命令返回内部“状态 + 是否真实删除 + 被删除用户”结果；只有成功提交的真实删除可触发事件，不改变外部 204/错误 envelope。
- 新增直接按目标用户 ID 投递的尽力 publisher/transport；使用 `CancellationToken.None`，异常只记 conversation/target/result 元数据并被吸收。
- 真实 SignalR 测试覆盖多连接目标、其他用户隔离、删除后事件与 NewMessage 停止、重复/并发删除一次事件、无成员/失败状态零事件、transport 失败仍 204 且数据库已撤权、日志脱敏。
- 新增 `DEC-015` 并更新工程方案、状态账本和本任务证据。

## 允许修改

- `src/RelayCove.Server/Hubs/IChatClient.cs`
- `src/RelayCove.Server/Realtime/`
- `src/RelayCove.Server/Services/ConversationCommandService.cs`
- `src/RelayCove.Server/Services/ConversationMemberRemovalResult.cs`
- `src/RelayCove.Server/Endpoints/ConversationEndpoints.cs`
- `src/RelayCove.Server/Program.cs`
- `tests/RelayCove.Server.Tests/`
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现客户端 deny-set、tombstone、缓存/通知清理、重新加入解封或权威列表对账。
- 不维护服务端 user→connection registry，不尝试主动移除未知 connection ID 的会话组或强制断连。
- 不把 SignalR 事件提升为授权真源，不改变 History/Send/Sync 的现有撤权行为。
- 不实现会话删除、Direct 成员变化、Public 个人状态删除、其他 realtime 事件、重试/outbox/backplane 或 migration。

## 验收标准

- [ ] 私有成员真实删除提交后，目标用户每条现有连接各收到一次正确 conversation ID；actor、其他成员和 outsider 不收到目标事件。
- [ ] 重复删除和并发同一目标只有赢得真实删除的命令发布一次；无成员、Public/Direct、无权限/未知会话和验证失败不发布。
- [ ] 事件到达后，同一旧连接不会再收到删除提交后开始的 NewMessage；事件丢失不改变既有 403/会话全集/Sync 权威补偿。
- [ ] transport/publisher 异常不回滚成员删除、不改变 204、不重试；日志不含 token、正文、显示名或用户名。
- [ ] 定向真实连接测试、既有回归、Fast/Full、model drift、漏洞审计、白名单、空白与固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --filter "FullyQualifiedName~AccessRevoked" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变已冻结的客户端先 deny-set/tombstone、事件尽力而为或撤权前在途帧语义。
- 需要服务端追踪连接、可靠事件 outbox、跨实例 backplane 或新的持久化结构。
- 必须与客户端清理同时实现才可满足服务端验收，导致范围跨越子系统。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（基线） | Debug 0 警告、0 错误；202 项测试通过 |

### 下一步

- 冻结 `DEC-015` 后实现真实删除提交结果、目标用户撤权事件与自动化验证。
