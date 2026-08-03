# 阶段 6：权威会话快照与 Sync 页原子提交

## 状态

- `completed`
- 分支：`agent/stage-6-client-sync`
- 基线：`ef69d87ade1a0bc50e5a683ee3a751e664908f31`

## 目标

交付客户端固定上界同步的两个不可拆安全边界：消费服务端既有显式 `Complete=true` 当前 actor 非分页权威会话全集，以单次本地对账事务登记新增/保留会话并撤销缺失会话；随后每个已验证 `SyncResponse` 页在一个 SQLite 写事务内复用现有唯一合并并仅在整页成功时推进 `LastSyncCursor`。本切片不启动后台 HTTP 重试循环。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `ef69d87`；前序本地缓存 Full 为 Release 0 警告/0 错误、244 项测试，原生 SQLite 安全版本、model drift 与八项目漏洞审计通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：服务端 `/api/sync` 已实现固定 SnapshotUpperBound、严格 NextCursor/HasMore 不变量、当前权限过滤和空洞前进；Shared 已有脱敏 `SyncResponse`，客户端尚无 HTTP 或 page commit。
- `已验证`：阶段 3 已新增 `ConversationListResponse`；ConversationQueryService 用单次列表查询构造 `Complete=true`，`GET /api/conversations` 返回该 wrapper，Shared round-trip 与 Server endpoint 测试均已覆盖。当前缺口只在客户端消费与本地原子对账。
- `已验证`：AccountScopedLocalCache 已有进程 scope gate、冷启动权威登记门、durable revocation intent/tombstone、两唯一键合并和 fatal fail-closed，但消息逐条各自提交且没有 LastSyncCursor API，不能用循环调用伪装整页原子提交。
- `已验证`：Claude #30 已将总调用数用尽到 `30/30`；本任务按账本降级 Codex 固定差异复核，不追加 Claude 调用。

## 范围

- 为既有 `ConversationListResponse(Conversations, Complete)` 增加脱敏 `ToString()` 并补协议回归；服务端 `/api/conversations` 的 Complete wrapper、权限和单查询语义保持不变。
- Client store 新增完整快照提交：拒绝 `Complete=false`、重复/非法 DTO；在 scope gate 下计算缺失会话，先把缺失项加入 deny-set并持久化 durable intents，再用一个立即写事务 upsert 当前会话、清除被权威确认重新加入者的 tombstone/deny、为缺失项写 tombstone并级联删除，最后整体替换本 store 的当前授权集合。
- 快照主事务或 intent 写入失败时整个 scope fatal fail-closed；失败不得部分授权、部分清 tombstone或继续同步。只有完整权威快照可清除重新加入会话的 tombstone，普通单会话登记仍不得清除。
- Client store 新增 LastSyncCursor 读取与整页提交。提交前验证消息严格递增和所有 cursor/upper/HasMore 不变量；续页必须原样使用同一 upper。事务内要求磁盘 cursor 等于 expected cursor、逐条复用 Realtime 的同一合并裁决、所有会话仍获授权，最后写入 NextCursor。
- 任一未知/revoked/fatal、协议错误、不可变载荷 Conflict、陈旧 expected cursor 或 SQLite 失败整页零提交且游标不变；Duplicate/PendingPromoted 属于成功合并。
- 真实磁盘测试覆盖快照新增/缺失/重新加入、intent 故障、账户隔离、整页回滚、空页越洞、续页上界、Realtime 先到 Duplicate、pending 提升、并发陈旧 cursor、重启 cursor 与日志脱敏。

## 允许修改

- `src/RelayCove.Shared/Conversations/`
- `src/RelayCove.Client/Storage/`
- `tests/RelayCove.Shared.Tests/Conversations/`
- `tests/RelayCove.Client.Tests/Storage/`
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现 HttpClient token/401 refresh、网络/429/5xx retry、指数退避、single-flight、SyncReason 合并、后台定时器或 SignalR 状态接线；这些依赖本切片的原子 API，紧接下一任务实现。
- 不实现未读/预览/通知候选、History、SendResponse、附件、read-through、WPF UI 或 tombstone 的任意非权威清除路径。
- 不改变服务端 `/api/conversations`、`/api/sync` 查询、数据库模型/migration、权限过滤、消息协议或默认 limit。
- 不把现有逐条 Realtime 合并改成绕过 scope gate 的第二套 SQL；Sync 必须复用同一个事务内裁决函数。

## 验收标准

- [x] 既有 `/api/conversations` 认证、权限、单查询和 `Complete=true` wrapper 回归不变；Shared round-trip 与新增脱敏 ToString 测试通过。
- [x] 完整快照一次提交新增/更新/撤销，缺失会话在对账期间立即 deny，提交后数据级联清除；权威重新加入可清 tombstone，普通登记仍被拒绝。
- [x] 每页消息合并与 LastSyncCursor 在同一立即写事务；Inserted/PendingPromoted/Duplicate 全部允许提交，Conflict/未知/撤权/陈旧 cursor/协议错误整页回滚。
- [x] 首页/续页、空页权限空洞、重启、Realtime 先到、并发页与账户隔离的真实磁盘测试通过，日志不含正文、会话名、用户 ID、数据库路径或 cursor 原值。
- [x] Client/Shared/Server 定向测试、Fast/Full、model drift、漏洞审计、白名单、空白与固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~SyncPage|FullyQualifiedName~ConversationSnapshot" --no-restore
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --filter "FullyQualifiedName~ConversationListResponse" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 无法在不新增第二套消息合并 SQL 的情况下让 Sync 整页共用现有 merge 裁决。
- 权威重新加入的 tombstone 清除无法与快照授权原子提交，或需改变 AccountScopeId/schema version。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 为 `ConversationListResponse` 增加脱敏输出，客户端只接受 `Complete=true` 且 DTO 唯一/合法的权威会话快照。
- 快照对账先建立进程 deny 与 durable revocation intent，再以一个 SQLite 立即写事务完成当前会话 upsert、重新加入解封、缺失会话 tombstone/级联删除和 intent 清理。
- 新增脱敏的游标读取/整页提交结果；Sync 页在一个事务中重用 Realtime 唯一合并裁决，只在全页成功时推进 `LastSyncCursor`。
- 代码检查点为 `cb7b1ed26dbbb934d92865af829beb159370abcf`；未改服务端协议、EF 模型、migration 或依赖。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `ef69d87`，前序 Full/244 项测试、原生 SQLite 安全版本、model drift 与漏洞审计通过 |
| `已验证` | Sync/快照定向 | Release `SyncPageCommitTests` 15/15、Storage 37/37、Shared 会话契约 3/3 |
| `已验证` | 故障与竞态 | 快照提交期间跨 store deny、同 cursor 并发仅一页提交、后续 Conflict 整页回滚，Release 每轮 3 项连续 5 轮通过 |
| `已验证` | Fast/Full | Debug/Release 均 0 警告、0 错误；Client 51 + Shared 32 + Server 175 + Updater 1 = 259 项测试全部通过 |
| `已验证` | 格式/空白/白名单 | `dotnet format --verify-no-changes`、`git diff --check` 通过；固定差异仅 8 个允许的 Client/Shared/测试文件 |
| `已验证` | EF model drift | `has-pending-model-changes --no-build` 返回自最新 migration 后模型无变化 |
| `已验证` | 依赖漏洞审计 | 8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 固定候选 Codex 复核 | 检查快照 intent/主事务失败窗口、授权/deny 更新顺序、page cursor 预条件、唯一合并顺序和整页回滚，无剩余发现；Claude 已达 `30/30` 硬上限，本任务不追加调用 |

### 下一步

- 快进集成本切片，随后实现 Sync HTTP 调度、401 refresh、有界网络重试、single-flight 与触发原因合并。
