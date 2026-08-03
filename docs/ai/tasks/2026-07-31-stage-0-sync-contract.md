# 阶段 0：同步、幂等与通知规格补丁

## 任务定义

- **任务名称：** 阶段 0 — 冻结消息同步契约
- **状态：** 已完成（候选验证与降级独立复核已通过）
- **基准提交：** `f87d8212c6e8600bc5c7d26f8aec52c3ce209f51`
- **原计划提交：** `0234a6b1d88dde92a958466b93f6e55a0ac04c18`（基准提交的直接子提交）
- **执行起点：** 本计划最终审查提交；执行者在编辑前用 `git rev-parse HEAD` 记录实际 SHA，避免在文件中自引用 HEAD
- **工作分支：** `agent/stage-0-sync-contract`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 4.2、8、9.3、10、11、12、13、20、21、24 节；[`Claude Max 二次评审`](../reviews/2026-07-31-claude-max-second-review.md)

### 目标

在业务代码出现前，把同步分页、发送幂等、本地消息合并、未读与通知处理、私有频道权限写成唯一、可编码且可转成自动化测试的规格，并新增 `DEC-003`。本任务完成独立复核后，下一任务直接创建可构建的解决方案脚手架，不再继续扩展元工作流。

### 已知事实

- `已验证`：方案已定义 `Messages.Id INTEGER PRIMARY KEY AUTOINCREMENT`、`UNIQUE(SenderId, ClientMessageId)`，且 `MessageDto` 含 `ClientMessageId`。
- `已验证`：当前方案仍使用 check-then-insert、`LastSyncedMessageId`、`IsNotified` 和孤立的 `LastNotifiedMessageId`，同步接口未定义分页快照。
- `已验证`：当前 `LocalMessages.Id` 同时被设计成服务端 `MessageId` 主键，但发送前的 pending 消息尚无服务端 ID；仅写“更新 pending”不足以实现无歧义合并。
- `已验证`：仓库尚无 `RelayCove.sln` 和业务代码，本任务只能完成规格一致性检查，不能声明构建或运行时行为通过。
- `已验证`：[SQLite 事务文档](https://sqlite.org/lang_transaction.html) 与 [AUTOINCREMENT 文档](https://sqlite.org/autoinc.html)（访问日期：2026-07-31）确认读事务看到固定快照、同一数据库同时只有一个写事务，且 `AUTOINCREMENT` 不复用已提交 ROWID；本协议仍把单服务端实例和消息不可变作为显式前提。
- `已验证`：[ASP.NET Core SignalR 官方文档](https://learn.microsoft.com/aspnet/core/signalr/groups?view=aspnetcore-10.0)（访问日期：2026-07-31）明确连接重建后不会保留组成员关系，服务端必须按权威权限重新加组；SignalR 仍只是实时通道。

### 假设与前提

- `假设`：第一版只有一个 RelayCove 服务端实例和一个 SQLite 主库；SQLite 写事务串行化，`Messages.Id` 的顺序可作为已提交消息的稳定扫描顺序。增加服务端写实例、更换数据库或允许 ID/提交乱序时，必须重新设计同步协议。
- `假设`：第一版消息写入后不可编辑、撤回或删除；若未来支持变更，必须新增变更流，不能复用只向前扫描的新消息游标。
- `假设`：`SyncPageSize` 默认 `100`、允许范围 `1..200`；`SyncPeriod` 默认 `60` 秒；`NotificationSummaryThreshold` 默认 `10`。三者都是可测试配置，不是长期架构常量。

### 范围

- 必须实现：
  - 修订同步请求/响应、固定 ID 上界分页、服务端扫描水位、客户端事务、失败重试、游标作用域与 single-flight。
  - 修订本地 pending/已发送消息身份、统一合并算法、发送幂等及并发重放语义。
  - 修订消息来源矩阵、未读边界、通知状态机、批量策略与恢复处理。
  - 修订私有频道加入、重新加入、历史懒加载、撤权、权威列表对账和缓存清理语义。
  - 新增 `DEC-003：消息同步、幂等与通知语义`。
  - 在工作流中增加决策记录触发条件，并清理 `CLAUDE.md` 的过时规范性表述。
- 允许修改：
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/WORKFLOW.md`
  - `docs/ai/STATUS.md`
  - `CLAUDE.md`
  - 本任务文件
- 明确不做：
  - 不创建业务代码、`RelayCove.sln`、`verify.ps1` 或 CI。
  - 不实现搜索、附件、备份、安装器或 Windows 通知探针。
  - 不新增依赖，不优化模型路由、Second Brain 或其他 AI 治理文档。
  - 不设计消息编辑、撤回、离线远程擦除或第二种数据库。
  - 不引入 outbox、消息队列、每频道同步游标或私有频道历史可见水位。

## 冻结契约

### 1. 同步请求、固定 ID 上界与扫描水位

`LastSyncedMessageId` 统一改为 `LastSyncCursor`。客户端不得用最后一条可见消息的 `MessageId` 推算游标；第一版游标类型是 `long`，但数值含义由服务端定义。

```text
GET /api/sync?cursor={long}&snapshotUpperBound={long?}&limit={int?}
```

```csharp
public sealed record SyncResponse(
    IReadOnlyList<MessageDto> Messages,
    long NextCursor,
    long SnapshotUpperBound,
    bool HasMore);
```

- 首页省略 `snapshotUpperBound`。服务端在同一只读事务中读取 `MAX(Messages.Id)`（空表为 `0`）作为 `SnapshotUpperBound`，并完成本页查询。
- 后续页必须原样携带服务端返回的 `SnapshotUpperBound`；每页只查询当前用户在该次请求时仍有权访问、且满足 `cursor < MessageId <= SnapshotUpperBound` 的同步候选消息。私有频道候选还必须满足 `MessageId > ConversationMembers.LastReadMessageId`；该条件只决定增量 Sync 是否返回消息，不限制 History/Search 对当前成员展示全部历史。本协议固定的是消息 ID 截止上界，不是在多个 HTTP 请求之间持有同一个数据库事务。
- 可见消息按 `MessageId ASC` 查询 `limit + 1` 条：
  - 存在第 `limit + 1` 条时，只返回前 `limit` 条，`HasMore=true`，`NextCursor=本页最后一条返回消息的 MessageId`。
  - 不存在更多可见消息时，`HasMore=false`，`NextCursor=SnapshotUpperBound`；即使末尾全是不可见消息或本页为空，也必须一次跨过这些全局 ID，不能空页死循环。
- 响应必须满足：消息 ID 严格递增且均在 `(cursor, NextCursor]`；`0 <= cursor <= NextCursor <= SnapshotUpperBound`；`HasMore == (NextCursor < SnapshotUpperBound)`；`HasMore=true` 时 `Messages` 非空且 `NextCursor > cursor`；只要 `SnapshotUpperBound > cursor`，`NextCursor` 就必须前进。
- `cursor < 0`、`limit` 越界或 `snapshotUpperBound < cursor` 返回可诊断的 `400`。无状态服务端无法证明客户端是否在续页篡改了上界，因此“续页原样回传”是受支持客户端的不变量，不是授权边界；服务端仍对每页重新鉴权。首次请求的 `cursor` 或续页上界大于服务端当前最大消息 ID 时返回 `409 SyncCursorInvalid`，客户端不得静默夹断游标。
- 同步查询始终执行服务端权限过滤。权限在分页过程中变化时，以每次请求的当前权限为准；加入前历史由 History 懒加载负责，撤权由事件和权威列表对账收敛。

### 2. 客户端同步事务、作用域与并发

- 本地数据库、文件缓存、通知 Group 和 `LastSyncCursor` 必须按账户隔离。稳定键定义为 `AccountScopeId = Base64UrlNoPadding(SHA256(UTF8(CanonicalServerBaseUri + "\n" + CurrentUserId.ToString("D").ToLowerInvariant())))`。`CanonicalServerBaseUri` 必须是无 user-info/query/fragment 的绝对 HTTP(S) URI：scheme 与 IDN host 小写、移除默认 `80/443` 端口、由 `System.Uri` 消解 dot-segment、保留反向代理子路径并统一为一个尾斜杠。数据库目录、缓存目录、Toast Group 与激活参数只使用 `AccountScopeId`，不得各自序列化元组。
- 切换服务器、账号或登出后不得复用另一作用域的消息、游标、未读、缓存或通知状态。通知激活处理器必须校验 `AccountScopeId` 与当前身份，并再次确认目标会话的当前权限。
- 客户端在写本地数据库前验证完整响应不变量。每页的消息合并、会话预览、未读派生状态和 `LastSyncCursor=NextCursor` 必须在一个本地事务提交。
- 任一非重复错误、协议不变量错误或本地数据冲突导致整页回滚；不得跳过坏消息后推进游标。重复消息只有在服务端 ID、幂等键和不可变字段相容时才是可忽略重复。
- 页面请求或本地提交失败时保留最后已提交游标。网络中断、超时、`429` 和可重试 `5xx` 使用指数退避加抖动，以相同 `(cursor, SnapshotUpperBound)` 重试；`401` 只允许刷新令牌一次再重试同一页；`400` 或响应不变量错误停止该轮并记录协议错误；`409 SyncCursorInvalid` 阻塞该作用域并提示需要受控重建，不得自动清除 pending 或静默归零。放弃当前轮次后，下一触发可以从最后已提交游标创建新上界；崩溃重启后丢失内存中的上界是安全的。
- 客户端循环到 `HasMore=false` 才算完整同步轮次。Toast、声音、闪烁等外部副作用只能在本地事务提交后执行；未完成轮次不得提前发批量通知。
- `Startup`、`Reconnect`、`WindowActivated`、`Periodic` 是 `SyncReason`，不是 `IncomingMessageSource`。每个同步作用域只允许一个同步循环；运行中触发只设置一次 pending rerun 并合并原因，当前轮结束后至多立即补跑一次，绝不并行分页。补跑时按当前状态裁定：窗口已激活则 `WindowActivated`，否则仍处首次启动恢复则 `Startup`，否则存在重连触发则 `Reconnect`，其余为 `Periodic`。登出或切换作用域必须取消旧循环。
- 每个完整同步轮次先获取权威会话列表，应用新增/撤权、静音和 `LastReadMessageId`，再拉消息页。第一版权威列表不分页：服务端在单个只读事务中返回当前成员关系全集并显式标记 `Complete=true`；客户端只有在响应校验与本地对账事务都成功后才可依据“缺失”推断撤权。若未来列表需要分页，必须先引入服务端生成的固定 `MembershipSnapshotToken`，所有页面共享同一成员快照，且只有服务端确认该快照已完整读取时才返回 `Complete=true`；不得拼接实时变化的普通分页结果。SignalR 每次重新连接后都按权威成员关系重新加入组，不信任旧连接的组状态。

### 3. 本地消息身份与唯一合并路径

`LocalMessages` 必须把本地主键与服务端消息 ID 分离，避免 pending 消息伪造服务端 ID：

```text
LocalId                    INTEGER PRIMARY KEY AUTOINCREMENT
ServerMessageId            INTEGER NULL UNIQUE
ClientMessageId            TEXT NOT NULL
SenderId                   TEXT NOT NULL
...
UNIQUE(SenderId, ClientMessageId)
```

- pending 行使用 `LocalId` 定位，`ServerMessageId=NULL`，`LocalSendStatus=Sending`；`MessageDto.Id` 落到 `ServerMessageId`。
- `Realtime`、`Sync`、`History`、`SendResponse` 全部调用同一个事务内合并函数，分别查询 `serverHit`（按 `ServerMessageId`）与 `keyHit`（按 `(SenderId, ClientMessageId)`）：
  - 两者均为空：插入已确认消息。
  - `serverHit` 为空、`keyHit=row`：仅当 `row.ServerMessageId` 为空、属于本账户 pending 且请求语义一致时，才允许 `PendingPromoted` 并补齐服务端字段；否则 `Conflict`。
  - `serverHit=row`、`keyHit` 为空：`Conflict`，禁止改写现有行的发送者或幂等键。
  - 两者命中同一行：所有不可变字段相容时为 `Duplicate`，否则 `Conflict`。
  - 两者命中不同本地行：`Conflict`。
- 不可变语义至少包括 `ServerMessageId`、`SenderId`、`ClientMessageId`、`ConversationId`、`Type`、`Content`、`ReplyToMessageId`、Attachment ID 集合与 Mention user ID 集合；`MessageDto` 必须携带合并所需字段。`Conflict` 作为数据完整性错误回滚并记录，不得自动任选一行。
- 未读递增、通知候选和会话预览更新只允许在“首次入库”或“pending 成功确认”的相应状态转换上发生一次；重复到达不得重复计数或通知。`IsRead=true` 与 `IsNotificationHandled=true` 都不得被后到来源重置为 `false`。
- 合并结果必须显式区分 `Inserted`、`PendingPromoted`、`Duplicate`、`Conflict`：只有他人消息的 `Inserted` 可以增加未读或登记通知候选；`PendingPromoted` 只确认自己的发送状态；`Duplicate` 不得重复执行增加未读、创建通知候选或更新预览等“到达型副作用”，但允许 History、已读推进或通知抑制执行 `false -> true` 的单调“观察型副作用”，并取消尚未派发的候选；`Conflict` 回滚。任何路径都不得把 `IsRead` 或 `IsNotificationHandled` 从 `true` 重置为 `false`。发送状态机固定为：`Sending -> Failed`（明确失败）、`Failed -> Sending`（用户显式重试并复用原 `ClientMessageId`）、`Sending/Failed -> Sent`（`200/201` 或 Realtime 权威确认）、`Sent -> Sent`（终态）；任何迟到失败只记日志。

### 4. 来源、未读与会话预览

`IncomingMessageSource` 只保留 `Realtime`、`Sync`、`History`、`SendResponse`。`NotificationPolicy` 只保留 `None`、`PerMessage`、`Summary`。

| 来源 | 本地合并 | 增加未读 | 通知决策 | 推进游标 | 更新会话预览 | 声音/闪烁 |
| --- | --- | --- | --- | --- | --- | --- |
| `Realtime` | 统一唯一键合并 | 仅首次入库、非本人、超过已读边界且非当前前台会话 | 提交后立即交给串行通知分发器 | 否 | 仅较新消息 | 成功通知后，本次消息最多一次 |
| `Sync` | 整页事务内统一合并 | 仅首次入库、非本人、超过已读边界且非当前前台会话 | 完整轮次后统一决策 | `NextCursor` 随页事务提交 | 仅较新消息 | 每轮最多一次 |
| `History` | 按需懒加载并统一合并 | 否 | 直接标记已处理 | 否 | 否 | 否 |
| `SendResponse` | 确认或合并 pending | 否 | 直接标记已处理 | 否 | 仅较新消息 | 否 |

“当前前台会话”必须同时满足：主窗口可见、未最小化、拥有前台焦点，且当前打开的会话 ID 与消息会话一致。`ConversationDto` 与 `LocalConversations` 必须包含 `LastReadMessageId`；它只表示已读边界，不参与历史可见性授权。消息 `Id <= LastReadMessageId` 时不增加未读、不通知。

`LastReadMessageId` 只能单调前进。`POST /api/conversations/{id}/read` 必须校验当前权限、目标消息确属该会话，并写入 `MAX(old, requested)`；不得接受任意极大 ID。前台会话收到新消息时也要在本地事务中标记已读/通知已处理，并异步上报新的 read-through ID。

同一成员生命周期内，客户端有效已读边界是 `MAX(localLastReadMessageId, serverLastReadMessageId)`，绝不因会话列表刷新而回退。本地推进时同时保存 `PendingReadThroughMessageId`；只有服务端确认值不小于该目标才可清除，上报失败按相同最大值幂等重试。撤权清理后的重新加入属于新成员生命周期，以服务端新基线重新初始化。

### 5. 通知唯一真源与恢复状态机

删除 `LastNotifiedMessageId`，将逐消息字段 `IsNotified` 统一改名为唯一真源 `IsNotificationHandled`：

- `false`：该消息尚未完成通知决策，或 Toast 提交发生可重试的临时失败。
- `true`：Toast 已成功提交给 Windows，或已经明确决定不提醒。
- 自己发送、History、已读边界内消息、当前前台会话、会话静音、全局免打扰、Windows 通知被用户禁用和 `NotificationPolicy.None` 都属于“明确不提醒”，在本地事务中置为 `true`，以后不补历史 Toast。
- 新通知候选先随消息以 `false` 提交；单实例内只有一个串行 `NotificationCoordinator` 可以扫描和处理候选，其他路径只能提交候选 ID，防止 Realtime、Sync 与恢复扫描同时重复调用 Toast。
- `PerMessage` 成功一条就将该条置为 `true`；`Summary` 成功后在一个本地事务中将本次汇总覆盖的全部消息置为 `true`。部分成功只提交已成功部分。
- 派发前必须再次确认消息仍未读、会话仍有效、未静音且用户没有打开对应会话。Toast API 的可重试临时失败保持 `false`，只在后续后台同步或下次启动恢复时重试，不做紧循环；明确的永久/配置性不可用记诊断并置为 `true`。平台调用无异常只表示“已接受”，不证明用户实际看见。声音或闪烁失败只记日志，不得把已成功 Toast 改回未处理。
- Toast 已被系统接受、但进程在本地置位前崩溃，可能导致恢复后重复提交一次；第一版接受这条 at-least-once 窗口，通知探针必须验证能否用稳定 tag/group 降低用户可见重复。

同步轮次维护两个候选集合：本轮由 Sync 首次插入或同步期间由 Realtime 首次插入的 `RoundCandidates`（每项保留首次来源），以及此前临时失败遗留的 `RecoveryCandidates`。同步运行期间 Realtime 不直接弹 Toast；完整轮次结束后统一去重和裁定。`Startup` 处理两者并集；后台 `Reconnect/Periodic` 在本轮权威会话列表成功对账后即可重试两者并集，不要求后续消息分页也完整成功；前台和 `WindowActivated` 的 `None` 只处理 `RoundCandidates`，`RecoveryCandidates` 保持 `false`，直到后台权威对账成功、Startup 或用户实际读到消息。

`NotificationCoordinator` 只能处理调用方提交的明确候选 ID；只有 Startup 或后台权威会话列表成功对账后显式构造 `RecoveryCandidates` 时才允许扫描该 `AccountScopeId` 下遗留的 `false`。Realtime kick 不得全表扫描，也不得提前取走正在进行的 Sync 候选。

协调器使用同一把 gate 原子完成“关闭当前同步轮次、截取并清空 RoundCandidates、切换 Realtime 分流状态”。Realtime 在 gate 内观察到轮次未关闭时加入本轮候选；观察到已关闭时走正常提交后即时派发，不存在既未入本轮、也未即时派发的中间状态。

同步轮次失败或取消时，协调器必须在同一 gate 内按首次来源拆分候选并关闭轮次：由 Realtime 首次插入的候选恢复为正常串行派发，立即按当前前后台/当前会话状态完成一次通知决策；由 Sync 首次插入的候选保持 `false` 并转入 `RecoveryCandidates`。这样永久 `400`、协议冲突或本地 poison row 不会无限扣住真实实时消息。最后一页提交后、通知决策前崩溃时，全部未处理项按恢复扫描规则处理。

同步轮次的通知策略固定为：

| 场景 | 候选处理 |
| --- | --- |
| `Startup` | 有候选时只发一条 `Summary` |
| `WindowActivated` | `None`，只更新未读并将本轮候选标记为已处理 |
| `Reconnect` / `Periodic`，窗口在前台 | 仅 `RoundCandidates` 使用 `None`；Recovery 保留 |
| `Reconnect` / `Periodic`，窗口不在前台且候选数 `1..10` | `PerMessage` |
| `Reconnect` / `Periodic`，窗口不在前台且候选数 `>10` | 一条 `Summary` |

阈值只统计过滤掉本人、已读、静音、免打扰和当前前台会话后的实际候选。一个同步轮次即使提交多条 Toast，声音与任务栏闪烁也最多触发一次；Realtime 单条消息按单条处理。

`PerMessage` 使用按会话稳定 Group：`Base64UrlNoPadding(SHA256(UTF8(AccountScopeId + "\n" + ConversationId.ToString("D").ToLowerInvariant())))`，Tag 为十进制 `ServerMessageId`；`Summary` Group 为 `Base64UrlNoPadding(SHA256(UTF8(AccountScopeId + "\nsummary")))`，Tag 固定为 `unread-summary`。按会话 Group 可在已读或撤权时直接清除整个会话的陈旧 Toast，不依赖仍存在的本地消息行；账户 Summary 则删除或按当前未读重建。通知激活目标是判别联合：`MessageTarget(AccountScopeId, ConversationId, MessageId)` 或 `UnreadOverviewTarget(AccountScopeId)`；跨会话 Summary 使用后者，不得伪造单一消息。稳定 Tag/Group 只能降低通知中心重复并支持已读/撤权清除，不能把崩溃窗口提升为严格 exactly-once。

### 6. 服务端发送幂等

- `POST /api/messages` 使用 INSERT-first。服务端拒绝 `Guid.Empty`；非法 GUID 由请求绑定返回 `400`。SQLite 中一律存储 `Guid.ToString("D").ToLowerInvariant()` 的规范文本，避免同一 GUID 的多种文本形式绕过 TEXT 唯一约束。
- 新插入在事务提交后返回 `201 Created`，只有赢得插入的请求才允许尝试一次 `NewMessage` 推送。推送失败只记日志，不回滚、不改变 HTTP 成功结果，依靠周期同步补偿。
- 发送权限校验必须在幂等回读之前，并与消息写入处于同一权威事务边界；撤权后的重放仍返回稳定的会话权限 `403`，不能借幂等命中读回旧消息。只捕获 `UNIQUE(SenderId, ClientMessageId)` 这一目标约束冲突，其他约束错误不得伪装成幂等重放。
- 命中目标唯一约束时在同一发送者范围回读原消息，返回 `200 OK`，不得再次推送。
- 重放请求的会话、类型、正文、回复目标、附件和提及必须与原请求语义一致；同一幂等键携带不同有效载荷时返回 `409 IdempotencyKeyReuse`，不得把旧消息伪装成新请求成功。
- SignalR 向会话组广播可以包含发送者连接，以支持发送者的其他设备；当前设备通过 `(SenderId, ClientMessageId)` 将 HTTP 响应与 Realtime 回声合并为同一条本地消息。
- SignalR 组不是授权边界，只能作为路由优化。每次创建新的实时投递时使用当前权威成员快照；发送路径一旦观察到撤权提交结果，不得再把该用户加入新的接收者集合。撤权前已经排队或在途的帧仍可能在提交后抵达，客户端 revoked/fail-closed 状态必须拒绝并且不得复活缓存；HTTP、History、Search、附件和后续 Sync 从撤权提交起严格拒绝访问。

### 7. 私有频道加入、历史与撤权

- 用户加入或重新加入私有频道后，只要当前仍是成员，就可通过 History/Search 查看全部历史；不全量回填，不增加 `JoinedAtMessageId` 等历史可见水位，也不让全局 sync 游标倒退。
- 添加成员、读取该会话当前 `MAX(Messages.Id)`（无消息为 `0`）、写入 `ConversationMembers.LastReadMessageId` 必须处于同一服务端写事务。该值是已读边界，不是可见性边界。
- 重复添加当前有效成员是幂等 no-op，不得重置其 `LastReadMessageId`；只有首次加入或移除后的重新加入才写入新的加入时间和当前已读边界。
- 权威会话 DTO 必须下发 `LastReadMessageId`。私有频道 Sync 在服务端排除 `MessageId <= LastReadMessageId`，因此加入前历史只经 History/Search 按需加载；客户端仍把同一边界作为防御性规则，任何来源意外带回较旧消息时都只能标记已读和通知已处理。加入后的新消息按正常规则增加未读和通知。
- `ConversationAccessRevoked(Guid conversationId)` 是尽力实时事件，不是授权真源。删除成员后，所有 History/Search/附件/发送接口立即按服务端当前成员关系返回带稳定错误码 `ConversationAccessRevoked` 的 `403`；客户端还必须在每轮同步前用权威会话列表对账，覆盖事件丢失和离线场景。第一版只有单个服务端读事务返回的非分页全集且 `Complete=true` 才是可做缺失判断的权威快照；超时、取消、解析失败或 `Complete=false` 不得触发清理。未来分页必须共享固定 `MembershipSnapshotToken`，不能把若干实时变化页面拼成撤权依据。普通、无法归因到撤权的 `403` 也不得直接触发破坏性清理。
- 撤权事件、权威列表缺失和稳定撤权 `403` 进入同一个幂等 `PurgeConversationAccess`。它先更新进程内 deny-set，并用独立最小事务持久化 revoked tombstone；消息入口、UI 和通知激活从此优先拒绝该会话。tombstone 成功提交是继续细粒度清理的必要检查点：若首次落盘失败，当前 `AccountScopeId` 立即进入 fatal fail-closed，本进程不得再展示该作用域的任何缓存内容。为覆盖随即崩溃的窗口，每次冷启动都必须先成功获取并提交 `Complete=true` 的权威会话对账，之后才允许加载或展示该作用域的私有缓存；离线或对账失败时只保持隐藏，不据此删除数据。tombstone 成功后才启动可重试清理流程，取消发送、History、上传下载和 UI/内存引用，删除消息、附件元数据、未读、通知候选及本地搜索数据，再按会话 Group 清除 Toast，并删除或重建可能包含其摘要的账户级 Summary Toast，最后尽力删除物理缓存。数据库、文件或 Windows 通知清理失败不得移除 deny-set/tombstone 或恢复 UI 访问；tombstone 只有在权威列表明确确认重新加入后才可删除。
- 统一消息入口不得根据未知 `ConversationId` 自动创建会话，也不得让迟到 Realtime/History 响应复活 revoked 会话；未知或 revoked 数据先拒绝入库并触发权威对账，只有服务端列表确认重新加入后才恢复接收。
- 离线设备无法远程擦除已落盘缓存是第一版已知限制。通知激活探针只冻结行为：无已有实例时，主实例完成账户上下文初始化后本地处理 `MessageTarget` 或 `UnreadOverviewTarget`；已有实例时，次实例通过探针选定的 IPC/`AppInstance` 转交完整目标，收到确认后退出。激活处理必须幂等，重复目标不得创建第二窗口或重复导航；具体由 `AppInstance.RedirectActivationToAsync` 还是 `Mutex + Named Pipe` 实现，留给探针裁定。

### 8. 决策与治理

`DEC-003` 在本任务完成时记为“已接受”，并注明它细化 `DEC-002`。它必须记录：

- 服务端扫描游标与固定消息 ID 上界分页不变量。
- 单服务端实例、SQLite 写事务串行化和消息不可变前提。
- 客户端逐页事务、作用域隔离、统一本地消息身份与合并规则。
- INSERT-first、`200/201/409`、提交后推送与周期同步补偿。
- `IsNotificationHandled` 唯一真源、串行通知分发和批量策略。
- 私有频道全部历史可见、打开时懒加载、`LastReadMessageId` 只管已读，以及撤权收敛规则。

`WORKFLOW.md` 的“记录与提交”必须明确：协议、数据库或兼容性发生变化时，同一任务必须更新 `DECISIONS.md`；改变本节任何前提时必须新增决策并链接被替代项。

任务完成后，本文件降级为历史执行证据；规范性真源是修订后的工程方案与已接受的 `DEC-003`，不得让任务文件长期成为第二套协议。

## 必须固化的契约测试清单

本任务不写测试代码，但工程方案必须列出可直接移植到后续 xUnit/集成测试的 Given/When/Then 场景：

1. 全局 ID 含大量无权限空洞或整页无可见消息时，最终 `NextCursor` 仍到达快照上限且不死循环。
2. 多页同步期间新提交消息不进入旧快照，在下一轮可拉到。
3. 本地某条消息写入失败时整页和游标均回滚；重试后不丢失、不重复派生副作用。
4. Realtime、Sync、SendResponse 以任意顺序并发到达，最终只有一条本地消息、一次未读、至多一次通知决策；用 barrier 覆盖“同步轮次关闭”与 Realtime 到达的原子边界。
5. pending 消息无服务端 ID也可持久化；响应/回声后只补齐同一行，不产生第二行。
6. 两个并发相同请求只产生一条服务端消息：一个 `201`、一个 `200`、只允许一次推送；相同键不同载荷返回 `409`。
7. 切换账号或服务器不会复用旧 `LastSyncCursor`；游标超出服务端最大 ID 时显式失败而非静默跳过。
8. Startup、WindowActivated、前后台 Reconnect/Periodic、阈值 `10/11` 和 Toast 临时失败分别得到确定策略。
9. 客户端游标为 `50`、私有频道加入前消息为 `60..90`、加入基线为 `90` 时，Sync 不返回 `60..90`，History/Search 可按需返回全部历史，`91` 之后的新消息正常同步与提醒；重新加入同理且不全量回填。
10. 撤权事件丢失或设备离线后，权威列表/`403` 仍能触发本地收敛；服务端从撤权提交起拒绝所有相关资源访问。
11. SignalR 重新连接后按当前权限重新加组；已撤权会话不会因旧组状态继续收消息。
12. 重复添加当前成员不会重置已读边界；较小的服务端 LastRead 不会覆盖本地 pending read-through；撤权后迟到 Realtime/History 不会复活缓存。
13. History 再次命中已由 Realtime 插入的未读行时，不重复到达型副作用，但可单调置为已读/通知已处理并取消未派发候选。
14. 权威会话列表读取期间成员新增、移除或排序变化，第一版单事务全集仍形成一致快照；未来普通分页结果不得触发 Purge。
15. Realtime 在 Sync 期间到达而该轮随后永久 `400` 或本地提交失败时，Realtime 候选仍会解闸并完成一次通知决策，Sync 候选留待恢复。
16. 清理任一步失败仍保持 deny-set/tombstone；按会话 Group 可清除已无本地行的陈旧 Toast，旧账号 Toast、迟到点击和重复激活不能打开当前账号同 ID 内容或创建第二窗口。
17. revoked tombstone 首次 INSERT 失败后立即崩溃并离线重启时，冷启动权威对账 gate 仍阻止旧私有缓存显示。

## 验收标准

- [ ] 当前规范性文档中的同步接口、DTO、字段、阶段任务和验收描述一致，不再使用 `LastSyncedMessageId`、`LastNotifiedMessageId`、`IsNotified`、`LatestMessageId` 或 `afterMessageId`。
- [ ] `SyncResponse` 四字段、请求参数、分页算法、不变量、空可见页、错误码、重试和客户端逐页事务均有明确语义。
- [ ] 本地消息具有独立 `LocalId` 和可空唯一 `ServerMessageId`；pending/响应/回声/补拉合并规则可直接转成测试。
- [ ] 来源行为矩阵、已读边界、通知恢复与四种 `SyncReason` 的确定策略完整，没有第二个通知真源或并行通知分发器。
- [ ] INSERT-first 的 `200/201/409`、并发冲突、载荷冲突、提交后推送和推送失败补偿明确。
- [ ] 私有频道加入、重新加入、全部历史懒加载、无可见性水位、撤权、`403`、权威对账和缓存清理边界一致。
- [ ] 工程方案包含上述契约测试清单；历史评审文件保持原样，不为通过搜索而改写历史证据。
- [ ] `DEC-003` 与 `WORKFLOW.md` 决策触发条件已落盘，`CLAUDE.md` 不再保留相反口径。
- [ ] `STATUS.md` 的下一任务改为“创建可构建解决方案和真实验证脚本”。

### 验证命令

```powershell
function Assert-Rg([string]$Pattern, [string]$Path) {
    & rg -q -- $Pattern $Path
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "缺少规范：$Pattern @ $Path（rg=$code）" }
}

Assert-Rg 'GET /api/sync\?cursor=' 'RelayCove_工程落地方案.md'
Assert-Rg 'SnapshotUpperBound' 'RelayCove_工程落地方案.md'
Assert-Rg 'LocalId' 'RelayCove_工程落地方案.md'
Assert-Rg 'ServerMessageId' 'RelayCove_工程落地方案.md'
Assert-Rg 'IsNotificationHandled' 'RelayCove_工程落地方案.md'
Assert-Rg 'ConversationAccessRevoked' 'RelayCove_工程落地方案.md'
Assert-Rg 'IdempotencyKeyReuse' 'RelayCove_工程落地方案.md'
Assert-Rg 'DEC-003' 'docs/ai/DECISIONS.md'
Assert-Rg 'DECISIONS\.md' 'docs/ai/WORKFLOW.md'
Assert-Rg '创建可构建解决方案和真实验证脚本' 'docs/ai/STATUS.md'
Assert-Rg 'LastSyncCursor' 'CLAUDE.md'
Assert-Rg 'IsNotificationHandled' 'CLAUDE.md'
Assert-Rg 'Named Pipe|AppInstance|IPC' 'CLAUDE.md'

$normativeFiles = @(
    'RelayCove_工程落地方案.md',
    'CLAUDE.md',
    'docs/ai/DECISIONS.md',
    'docs/ai/STATUS.md'
)
$legacy = & rg -n -- "LastSyncedMessageId|LastNotifiedMessageId|IsNotified|LatestMessageId|afterMessageId|只用 Mutex|Mutex \+ 激活窗口|激活窗口即可" @normativeFiles 2>&1
$legacyExit = $LASTEXITCODE
if ($legacyExit -eq 0) { $legacy; throw "发现旧同步或通知口径" }
if ($legacyExit -gt 1) { $legacy; throw "rg 执行失败，退出码 $legacyExit" }

git diff --check $ExecutionBase --
if ($LASTEXITCODE -ne 0) { throw '工作差异存在空白错误' }
git diff --check "$ExecutionBase..HEAD"
if ($LASTEXITCODE -ne 0) { throw '已提交差异存在空白错误' }

$allowedFiles = @(
    'RelayCove_工程落地方案.md',
    'CLAUDE.md',
    'docs/ai/DECISIONS.md',
    'docs/ai/STATUS.md',
    'docs/ai/WORKFLOW.md',
    'docs/ai/tasks/2026-07-31-stage-0-sync-contract.md'
)
$changedFiles = @(git -c core.quotepath=false diff --name-only "$ExecutionBase..HEAD")
$unexpectedFiles = @($changedFiles | Where-Object { $_ -notin $allowedFiles })
if ($unexpectedFiles.Count -gt 0) { throw "发现范围外文件：$($unexpectedFiles -join ', ')" }
$missingFiles = @($allowedFiles | Where-Object { $_ -notin $changedFiles })
if ($missingFiles.Count -gt 0) { throw "应更新但未进入最终差异：$($missingFiles -join ', ')" }
```

所有正向规范按目标文件逐项断言；旧口径检查把 `rg` 的“无匹配”退出码 `1` 转成成功路径。历史任务与评审允许保留旧字段作为证据。本任务不得声明 `dotnet build` 或自动化测试通过。

### 停止并询问

- 仓库实际 DDL、DTO 或成员权限模型与“已知事实”冲突。
- 产品要求改变“加入后可查看全部历史、打开时懒加载、无历史可见水位”或“只保留逐消息通知真源”的裁定。
- 必须引入新的数据库、依赖、outbox、消息变更流，或改变第一版单实例/SQLite 前提。
- 发现任务范围外的未提交修改、密钥、破坏性操作或无法解释的规范冲突。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行步骤

1. 按顺序完整阅读 `AGENTS.md`、`docs/ai/WORKFLOW.md`、工程方案相关章节、`docs/ai/DECISIONS.md`、`docs/ai/STATUS.md` 和本任务文件。
2. 执行 preflight：确认工作区干净、当前分支准确、`0234a6b` 是 HEAD 祖先；设置 `$ExecutionBase=(git rev-parse HEAD).Trim()`，写入任务结果的“执行起点”。检查 `0234a6b..$ExecutionBase` 之间除本任务最终计划修订外没有其他文件变化，否则停止询问。
3. 把任务和 `STATUS.md` 更新为“进行中”，创建一个只含开工元数据的本地提交。
4. 按三个有序本地检查点实现并分别自审/提交：
   1. 同步协议、`LocalMessages` 身份与发送幂等。
   2. 未读、通知状态机、账户隔离与私有频道权限。
   3. `DEC-003`、`CLAUDE.md`、`WORKFLOW.md`、`STATUS.md` 和任务交接一致性。
5. 运行全部文档断言、旧口径检查、文件白名单和 `git diff --check`，填写候选结果。
6. 按 `REVIEW_TEMPLATE.md` 独立审查 `$ExecutionBase..HEAD`；发现问题后修复、重新验证并创建后续本地提交，直到完整范围无阻塞项。
7. 最后创建仅记录复核证据、任务“已完成”和下一任务的元数据提交。该提交不得改变契约；若改变契约，必须重新进入第 6 步。最终再次运行范围与空白检查。不得自行推送或合并。

### Preflight 命令

```powershell
if ((@(git status --porcelain)).Count -ne 0) { throw '开工前工作区不干净' }
if ((git branch --show-current).Trim() -ne 'agent/stage-0-sync-contract') { throw '当前分支错误' }

git merge-base --is-ancestor 0234a6b1d88dde92a958466b93f6e55a0ac04c18 HEAD
if ($LASTEXITCODE -ne 0) { throw '原计划提交不是当前 HEAD 的祖先' }

$ExecutionBase = (git rev-parse HEAD).Trim()
$planDrift = @(git diff --name-only "0234a6b1d88dde92a958466b93f6e55a0ac04c18..$ExecutionBase")
$unexpectedPlanDrift = @($planDrift | Where-Object {
    $_ -ne 'docs/ai/tasks/2026-07-31-stage-0-sync-contract.md'
})
if ($unexpectedPlanDrift.Count -gt 0) {
    throw "计划审查期间出现范围外变化：$($unexpectedPlanDrift -join ', ')"
}
```

若执行会话重启，从任务结果中恢复并重新赋值 `$ExecutionBase`，不得用新的 HEAD 覆盖原执行起点。

## 执行提示词

```text
按顺序阅读 AGENTS.md、docs/ai/WORKFLOW.md、RelayCove_工程落地方案.md 的相关章节、docs/ai/DECISIONS.md、docs/ai/STATUS.md 和本任务文件。
只实现本任务“范围”与“冻结契约”，不处理“明确不做”的事项。
先完成 preflight 并记录 ExecutionBase，再修订规格；不要创建业务代码或占位验证脚本。
把契约同步到工程方案、DEC-003、CLAUDE.md、WORKFLOW.md、STATUS.md，避免多份规范互相矛盾。
按三个检查点创建可审查的本地提交，运行列出的文档检查；未运行项目标注“未验证”。再按 REVIEW_TEMPLATE.md 独立复核完整 ExecutionBase..HEAD；不得推送或合并。
触发停止条件时保留现场并询问，不自行扩大范围。
```

## 任务结果

### 执行起点

- `ExecutionBase`：`e1ad7e6fae184d244dffe5d120794f10f391cd33`。

### 修改摘要

- 工程方案已定义固定消息 ID 上界分页、逐页本地事务、single-flight、账户作用域和游标错误处理。
- 本地消息身份已分离为 `LocalId` / `ServerMessageId`，四来源合并与 INSERT-first `200/201/409` 语义已统一。
- 未读、通知候选 gate、恢复策略、私有频道历史和撤权 fail-closed 规则已写入规范，并列出 17 个后续契约测试场景。
- `DEC-003`、执行工作流、Claude 仓库指引和状态页已同步；任务已在固定 `ReviewHead` 上完成候选验证与独立只读复核。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 规格一致性 `rg` | `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc`；13 项正向断言通过，规范性文件旧口径匹配数为 0 |
| `已验证` | `git diff --check` 与文件白名单 | 工作差异和 `ExecutionBase..ReviewHead` 已提交差异均通过；最终范围恰为允许的 6 个文件 |
| `未验证` | Claude 前置 challenge | 2026-08-03 调用失败：本机认证源优先导致组织连接器被禁用；调用前后 HEAD 与干净状态一致，工具未返回 `workspace_root`、模型、`model_mismatch` 或费用 |
| `已验证` | 独立只读复核 | Claude 候选审查因同一认证问题未返回结构化结果；调用前后 `ReviewHead` 与干净状态一致。按降级规则执行 Codex 只读复核：两处 `SyncResponse`、三处同步端点与 17 个契约场景一致，未发现可操作 P0-P3 |
| `未验证` | `dotnet build` | 本任务无解决方案，不适用 |

### 文件范围

- 新增：无。
- 修改：`RelayCove_工程落地方案.md`、`CLAUDE.md`、`docs/ai/DECISIONS.md`、`docs/ai/STATUS.md`、`docs/ai/WORKFLOW.md`、本任务文件。
- 删除：无。

### 决策与限制

- 决策：执行期间以本任务为变更目标；完成后工程方案是当前可执行规范，已接受的 `DEC-003` 记录决策依据，本任务仅为历史证据。三者出现冲突时停止处理，不自行选择。
- 已知限制：本任务只建立规范，不证明运行时行为；服务端数据回滚/恢复后的同步世代切换留给备份恢复任务，当前只要求 `cursor > MAX(Id)` 显式失败。

### 下一步

- 创建 `RelayCove.sln`、四个源项目、测试项目与真实 `Fast`/`Full` 验证脚本，并完成正向和负向验证。
