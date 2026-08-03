# 阶段 4：文字消息入库与历史 API

## 任务定义

- **任务名称：** 阶段 4 — 文字消息存储、INSERT-first 幂等发送与 keyset 历史
- **状态：** 进行中
- **基准提交：** `4309fe4b729c5eef063bab4903def5776e57409e`
- **工作分支：** `agent/stage-4-text-message-api`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.4、8.2、10.1/10.2、11.1/11.2、12.1/12.2、阶段 4；`DEC-002`、`DEC-003`、`DEC-005`、`DEC-009`

### 目标

形成第一个可靠文字消息 HTTP 闭环：当前有权用户以 `(SenderId, ClientMessageId)` 在权威事务内 INSERT-first 发送，201 新建、200 相同载荷重放、409 不同载荷复用；当前有权用户以消息 ID keyset 拉取历史。Messages 上线的同一变更必须让私有成员加入基线读取事务内当前最大消息 ID，并让会话列表投影真实最后消息/未读聚合。

### 已知事实

- `已验证`：基准 `4309fe4` 的 Fast 通过，Debug 构建 0 警告、0 错误；Server 113、Shared 19、Client/Updater 各 1，共 134 项测试通过，工作树干净。
- `已验证`：`DEC-003` 已冻结消息不可变、`UNIQUE(SenderId, ClientMessageId)`、权限先于幂等回读、201/200/409、固定 ID 游标和私有加入水位；阶段 5 才实现 SignalR，阶段 6 才实现客户端 Sync/合并。
- `已验证`：阶段 3 的 `ConversationAccessQuery` 是 Public/Private/Direct 当前内容可见性真源，可组合进消息 SQL；命令写入沿用 SQLite 非 deferred Serializable 事务与动态 actor 复核。
- `已验证`：EF Core 10 SQLite 对非复合数字主键默认启用 AUTOINCREMENT；SQLite 官方保证 AUTOINCREMENT 不复用已提交 ROWID 且单调增加但允许空洞，符合固定消息游标前提。
- `已验证`：EF Core 官方建议 ID keyset/seek 分页并要求完全唯一排序；History 以单调唯一消息 ID 为游标，无需 offset。
- `已验证`：当前没有 Attachments 表、消息推送器、around/read/Sync 端点或客户端缓存；本切片不得伪造这些能力。
- `已验证`：Claude XHigh challenge #22 在 60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，没有返回模型、workspace、费用或结论；按用户要求不重试，不阻塞 Codex 依据仓库与官方证据冻结 `DEC-010`。

### 已冻结契约

- `已验证`：Shared 固定 `MessageType` 为 Text=1、Image=2、File=3、System=4；本切片的用户发送只接受 Text。Image/File 等附件存储完成后开放，System 只允许未来受控服务端生成；非 Text 请求返回稳定 `409 MessageTypeUnsupported`。
- `已验证`：Text `Content` 保留原始有效 UTF-16/换行语义，要求 1–4000 Unicode scalar value且至少一个非空白字符；允许 `TAB/CR/LF`，拒绝其他 Unicode Control。幂等比较使用保存后的精确字符串，不 trim、不规范化。
- `已验证`：MentionUserIds 作为无序集合比较，最多 20 个、不得含空值或重复；目标必须是当前正常用户且对该会话有内容访问权。ReplyToMessageId 必须大于 0并属于同一会话。附件 ID 列表在本切片必须为空。
- `已验证`：Messages 的 Sender/User 外键使用 Restrict，Reply 使用 NO ACTION（阻止单独删除被回复消息，同时允许 Conversation 硬删在语句末级联整组消息），Conversation 硬删 Cascade；MessageMentions 随 Message 硬删 Cascade、MentionedUser 使用 Restrict，避免用户硬删改变不可变消息载荷。常规会话仍只软删，消息不提供编辑、撤回或删除端点。
- `已验证`：`GET /api/conversations/{id}/messages?beforeMessageId=&limit=` 默认 50、范围 1..100，以 `Id < before` 读取 `limit+1` 条；响应按 ID 升序，`NextBeforeMessageId` 指向本页最旧 ID，`HasMore` 明确是否可继续。
- `已验证`：发送/历史 MessageDto 的 Attachments 在本切片固定为空集合；新消息提交时更新 Conversation.UpdatedAt。会话列表的 LastMessageId 取当前最大 ID，UnreadCount 只统计他人且超过当前成员水位的消息；Public 无状态行时水位为 0。

### 范围

- 必须实现：
  - Shared `MessageType`、`SendMessageRequest`、`MessageDto`、空附件 DTO 依赖和历史响应契约；新增必要稳定错误码。
  - Server `Message` / `MessageMention` 实体、DbContext 映射、AUTOINCREMENT、CHECK/唯一/索引/外键与显式 migration。
  - `POST /api/messages`：动态权限、文本/回复/提及校验、目标唯一冲突捕获、201/200/409 和安全日志。
  - `GET /api/conversations/{id}/messages`：访问过滤与消息投影在同一权威查询边界，ID keyset、稳定排序和分页元数据。
  - 私有成员 add/rejoin 在同一写事务读取当前 `MAX(Messages.Id)`；重复成员不重置。会话列表投影真实 LastMessageId/LastReadMessageId/UnreadCount。
  - 真实 SQLite migration up/down/model drift、旧 Users/Conversations 数据保留、约束/外键/AUTOINCREMENT 不复用，以及真实 HTTP 幂等并发/撤权/历史/日志测试。
  - 新增决策记录，冻结文本载荷、消息删除/外键、幂等比较和历史分页边界。
- 允许修改：
  - `src/RelayCove.Shared/**`
  - `src/RelayCove.Server/**`
  - `tests/RelayCove.Shared.Tests/**`
  - `tests/RelayCove.Server.Tests/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - 本任务文件
- 明确不做：
  - 不实现 Image/File/System 发送、Attachments 表/上传下载、缩略图或附件 DTO 的真实内容；后续附件切片处理。
  - 不实现 around、read-through、Search、`GET /api/sync`、客户端 SQLite/合并/通知；按阶段 4/6 后续切片处理。
  - 不实现 SignalR `NewMessage` 或 outbox；阶段 5 处理。本切片的“新建只推送一次”只固定可由后续推送层识别的 Created/Replay 结果。
  - 不实现消息编辑、撤回、删除、频道删除或用户硬删除流程，不引入新依赖，不改变单实例单 SQLite 与显式 migration 边界。

### 验收标准

- [x] migration 在真实 SQLite 创建 Messages/MessageMentions、AUTOINCREMENT、唯一/索引/CHECK/外键，能回滚且保留旧认证/会话数据，model drift 为 false。
- [x] 有权用户可发送有效 Text 并返回 201；相同幂等键和相同语义顺序/并发重放只生成一行并返回 200，载荷不同返回 `409 IdempotencyKeyReuse`。
- [x] 权限检查先于幂等回读；撤权后首次发送和旧键重放均稳定 `403 ConversationAccessRevoked`，不存在消息/用户/SQLite 细节泄漏。
- [x] Reply 必须同会话；mentions 是至多 20 个当前可访问正常用户的集合；非 Text、非空 AttachmentIds、无效/空白/过长/控制字符文本稳定失败。
- [x] 当前有权用户按唯一 ID keyset 拉取升序历史，无权/撤权用户 403；页间不重复、不跳过，边界和默认/上限有自动化验证。
- [x] 新消息更新会话最后消息与他人未读；私有成员首次加入/重新加入写入事务内当前最大消息 ID，重复 upsert 不回退水位。
- [x] Full、漏洞审计、文件白名单、`git diff --check` 与固定差异独立复核通过或按规则如实记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check '4309fe4b729c5eef063bab4903def5776e57409e..HEAD'
```

### 停止并询问

- 证据要求改变 `DEC-003` 的不可变消息、权限先于回读、幂等/游标语义，或改变单 SQLite 写实例、显式 migration 边界。
- 必须提前引入附件存储、SignalR/outbox、客户端同步、大型依赖，或允许消息编辑/删除和 ID 复用。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现 Text 的存储、INSERT-first 发送、History 和会话聚合，不提前实现 around/read/Sync/SignalR/附件。
发送事务先动态检查 ConversationAccessQuery，再处理 reply/mentions 和目标唯一冲突；撤权后不得通过旧幂等键读回消息。
消息 ID 必须由真实 SQLite AUTOINCREMENT 生成并验证不复用；History 只用唯一 ID keyset。
Claude 仅作一次 60 秒参考，不重试、不阻塞 Codex；绿色后按授权仅快进集成并推送，不触碰 main/Tag/Release/部署。
```

## 任务结果

### 修改摘要

- 新增 Shared `MessageType`、发送/消息/历史/附件占位 DTO，并为请求和响应固定敏感载荷脱敏字符串表示及 `MessageTypeUnsupported` 错误码。
- 新增不可变 Message/MessageMention 实体、SQLite AUTOINCREMENT migration、唯一/CHECK/索引与明确 Cascade/Restrict/NO ACTION 外键；真实验证 committed ID 不复用、整会话级联及单独删除被回复消息受限。
- 新增 `POST /api/messages`：同一 Serializable 写事务内权限优先、reply/mention 校验、INSERT-first、仅目标唯一冲突回读、精确集合幂等、201/200/409 与安全日志。
- 新增单个权威 SQL 完成当前权限与 History 页的 keyset 查询，避免访问检查与消息读取之间的撤权 TOCTOU；响应升序、游标排除边界、默认 50、范围 1–100。
- 会话列表投影真实 `LastMessageId`/他人 `UnreadCount`；私有成员首次加入和重新加入在成员写事务内取当前 `MAX(Messages.Id)`，重复 upsert 保留已有水位。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 Fast | Debug 0 警告、0 错误；134 项测试通过 |
| `已验证` | 最终 Fast | Debug 0 警告、0 错误；Server 131、Shared 23、Client 1、Updater 1，共 156 项测试通过 |
| `已修复` | 首轮 Full | formatter 发现 Windows 工作副本 CRLF 及 EF migration 的 BOM/块级 namespace；运行仓库 formatter 后复跑，无行为失败 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Full` | format clean；Release 0 警告、0 错误；156 项测试通过；`git diff --check` 通过 |
| `已验证` | 真实 SQLite migration/模型 | Stage 3→最新→Stage 3→Initial→空库迁移通过并保留旧认证/会话数据；AUTOINCREMENT 不复用 committed ID；model drift 为 false |
| `已验证` | 消息 HTTP/服务场景 | Text 201；顺序/并发同载荷 200 且单行；集合换序等价；不同载荷 409；相同 key 不同发送者 201；撤权后旧/新 key 均 403；busy 503 |
| `已验证` | History/聚合/成员水位 | 单 SQL 权限化 History、空会话 200、无权 403、升序 keyset 无重复/跳过、多 mention 不截断；最后消息、本人排除未读、首次/重新加入最大 ID 与重复 upsert 不回退通过 |
| `已验证` | 实体/数据库边界 | Unicode scalar/空白/控制字符/mention 上限、持久后不可变、sender/mention 限制、reply NO ACTION 与 conversation cascade 通过 |
| `已验证` | `dotnet list RelayCove.sln package --vulnerable --include-transitive` | 8 个项目均无已知易受攻击的直接或传递包 |
| `未验证` | Claude XHigh challenge #22 | 60 秒内因认证源优先级超时，无模型、workspace、费用或结论；按用户要求不重试、不阻塞 |
| `已验证` | Codex 固定差异复核 | `ReviewBase=e677597`、`ReviewHead=391aff0`；协议/授权/幂等/迁移/日志/取消/文件白名单与空白检查无剩余发现 |

### 文件范围

- 新增：消息 Shared 契约；Server Message/MessageMention、migration、发送/History endpoint 与服务；消息实体、SQLite、HTTP 与契约测试。
- 修改：工程方案、决策/状态/执行/任务文档；Conversation/User 导航、DbContext/Program、会话成员水位与列表聚合、错误码和既有 migration 契约测试。
- 删除：无。

### 决策与限制

- 决策：`DEC-010`。消息 committed ID 永不复用；Text 精确保留且不可变；权限先于幂等回读；mentions 按无序集合比较；History 只用唯一 ID keyset。Reply 用 NO ACTION 而非立即 RESTRICT，以同时阻止单条被引用消息删除并允许一条 Conversation 硬删语句完成整组级联。
- 已知限制：本切片不支持 Image/File/System 发送、真实附件、around/read、Sync/Search、SignalR/outbox、客户端缓存/合并/通知；History 当前最多 100 条且按请求重新检查权限。
- Claude 未返回第二意见；最终结论由 Codex 结合仓库、官方 SQLite/EF Core 证据、真实 SQLite/HTTP 自动化与固定差异复核独立承担。

### 下一步

- 仅快进 `agent/v1-integration` 并推送，随后拆分阶段 4 around/read 与固定上界 Sync 的下一个纵向任务。
