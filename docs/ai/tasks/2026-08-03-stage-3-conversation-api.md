# 阶段 3：会话访问与成员管理 API

## 任务定义

- **任务名称：** 阶段 3 — 会话创建、权威列表、成员管理与动态访问校验
- **状态：** 已完成
- **基准提交：** `9c4f4d4eab931d03071c21fcf6c19de440fcd661`
- **工作分支：** `agent/stage-3-conversation-api`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.2、7.3、8.2、10.2、阶段 3；`DEC-003`、`DEC-006`、`DEC-008`

### 目标

提供阶段 3 可独立验证的 HTTP 闭环：全局管理员创建公共/私有频道，普通用户创建或获取唯一 Direct，当前用户读取 `Complete=true` 权威会话全集与单个会话，授权管理员维护私有频道成员；撤权提交后相关读取立即稳定返回 403。

### 已知事实

- `已验证`：基准 `9c4f4d4` 的 Fast 通过，Debug 构建 0 警告、0 错误；Server 98、Shared 16、Client/Updater 各 1，共 116 项测试通过，工作树干净。
- `已验证`：Conversations/ConversationMembers migration、三种类型、会话内 Member/Administrator、Direct 永久 canonical pair key、复合成员主键、软删除、外键和非负单调已读边界已在真实 SQLite 通过。
- `已验证`：`DEC-003` 要求不分页的会话全集在单个权威读取中显式返回 `Complete=true`；私有撤权由全集缺失和稳定 `403 ConversationAccessRevoked` 收敛，当前成员仍可懒加载全部历史。
- `已验证`：阶段 3 要求公共频道所有正常用户可见、私有频道只对成员可见、Direct 只对两名参与者可见；全局管理员创建频道并管理私有成员，创建/获取一对一私聊必须并发单例。
- `已验证`：当前 schema 尚无 Messages，任何成员加入时数据库可观察的当前会话最大消息 ID 必然为 0；阶段 4 引入 Messages 的同一代码变更必须把初始化切换为事务内真实 `MAX(Id)`。
- `已验证`：现有 JWT bearer 每次请求动态确认用户仍存在且未禁用；全局管理员 policy 与管理员用户创建服务还会在写事务内复核 `Users.IsAdmin`，SQLite busy/locked 已统一为稳定 503。
- `已验证`：Microsoft.Data.Sqlite 默认提供 Serializable 事务且 SQLite 同时只有一个待提交写者；非 deferred 事务在开始时取得写锁。ASP.NET Core 资源授权需要在资源加载后命令式执行；EF Core split query 在并发更新下可能返回不一致结果，因此权威集合和授权投影保持单查询。
- `已验证`：Claude XHigh challenge #21 在 60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，没有返回模型、workspace、费用或结论；按用户要求不重试，不阻塞 Codex 依据仓库与官方证据冻结 `DEC-009`。

### 已冻结契约

- `已验证`：`POST /api/conversations` 使用一个判别请求：频道传 `Type` + `Name` 且不传 `ParticipantUserId`；Direct 传 `Type=Direct` + `ParticipantUserId` 且不传 `Name`。频道仅全局管理员可创建；任意正常认证用户可创建/获取自己的 Direct。
- `已验证`：新建频道返回 201；新建 Direct 返回 201，已存在或从软删除恢复同一 Direct 返回 200，始终复用同一会话 ID。创建频道时创建者成为该频道 `Administrator`；Direct 恰好创建两个 `Member`。
- `已验证`：私有频道成员管理允许数据库当前全局管理员，或该私有频道当前 `Administrator`；全局管理员的管理覆盖不自动授予其读取私有内容的权限。普通私有成员管理返回 `403 AccessDenied`，非成员或已撤权用户访问资源返回 `403 ConversationAccessRevoked`。
- `已验证`：`POST /api/conversations/{id}/members` 是幂等 upsert：新成员 201，已有同角色或角色更新 200；`DELETE` 对已不存在成员幂等 204。仅 PrivateChannel 支持成员写操作；PublicChannel 的访问是隐式的，Direct 成员不可变，类型冲突返回稳定 409。
- `已验证`：`GET /api/conversations` 返回 `ConversationListResponse(Conversations, Complete=true)`；`ConversationDto` 延续规范字段，Messages 上线前 `LastMessageId/UnreadCount=0`，公共非显式成员的 `LastReadMessageId=0`。Direct `Name` 按当前用户动态取另一参与者 DisplayName。
- `已验证`：`GET /api/conversations/{id}/members` 只支持 Private/Direct；公共频道没有可伪造 JoinedAt 的“全体隐式成员”列表，返回类型冲突。私有成员和全局管理员可读私有成员清单，Direct 仅参与者可读。
- `已验证`：会话与成员命令在 SQLite Serializable 非 deferred 写事务内复核 actor、目标用户、会话类型和角色；日志只记录用户/会话 ID、类型、角色和结果，不记录请求对象或显示名。

### 范围

- 必须实现：
  - Shared 创建会话、权威列表、会话 DTO、成员 upsert/list DTO 与新增稳定错误码。
  - `POST/GET /api/conversations`、`GET /api/conversations/{id}`、`GET/POST /api/conversations/{id}/members`、`DELETE /api/conversations/{id}/members/{userId}`。
  - 频道创建的动态全局管理员复核；Direct canonical 单例、软删恢复、两名 Member 与并发冲突归一化。
  - 公共/私有/Direct 的动态查询权限、Direct 个性化名称、`Complete=true` 单语句权威列表和撤权 403。
  - 私有成员 add/rejoin/role update/remove 的事务内 manager 复核与当前 schema 初始 `LastReadMessageId=0`。
  - 真实 HTTP + SQLite 自动化测试认证、创建、并发、列表、成员管理、撤权、错误 envelope、取消/锁和日志边界。
  - 新增决策记录，冻结公共 API、管理授权和阶段 4 消息水位衔接。
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
  - 不实现 `PUT/DELETE /api/conversations/{id}`、公共频道显式成员管理、用户目录/搜索、个人资料或管理员 UI。
  - 不创建 Messages/MessageMentions，不实现消息发送、History/Sync/read、真实未读计数或 `LastMessageId`；阶段 4 处理。
  - 不实现 SignalR 组或 `ConversationAccessRevoked` 实时事件；阶段 5 处理，当前以 HTTP 403 + 权威列表收敛。
  - 不实现客户端缓存/UI，不引入新依赖，不改变 JWT/refresh、单进程单 SQLite 写实例或显式 migration 边界。

### 验收标准

- [x] 未认证请求统一 401；只有数据库当前全局管理员可创建频道，创建者拥有频道 Administrator 成员行。
- [x] 所有正常用户都在权威列表看到非删除公共频道；私有/Direct 仅当前成员可见，响应 `Complete=true`，Direct 双方看到对方昵称。
- [x] 同一 pair 的正序、反序和并发 Direct 创建永久只得到同一 ID 与恰好两个 Member；软删除后恢复原 ID，自聊/空/禁用目标稳定失败。
- [x] 全局管理员或当前频道 Administrator 可新增、重新加入、更新角色和移除私有成员；初始水位为 0，重复操作幂等，普通成员/非成员/错误类型使用稳定 403/409。
- [x] 私有成员撤权提交后，单会话、成员列表与后续消息资源的共享访问检查立即返回 `403 ConversationAccessRevoked`，权威全集不再包含该会话。
- [x] 请求校验使用 camelCase Details；错误/日志不泄露请求对象、昵称或 SQLite 细节；busy/locked 保持 503。
- [x] Fast、Full、漏洞审计、文件白名单、`git diff --check` 与固定差异独立复核通过或按规则如实记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check '9c4f4d4eab931d03071c21fcf6c19de440fcd661..HEAD'
```

### 停止并询问

- 证据要求改变公共/私有/Direct 可见性、`DEC-003` 权威全集/撤权语义、`DEC-008` Direct 永久唯一或阶段 4 消息水位前提。
- 必须引入外部身份/消息基础设施、大型依赖，或允许全局管理员在未加入时直接读取私有消息内容。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现阶段 3 会话 HTTP 访问与私有成员管理，不提前实现消息、SignalR、客户端或频道更新/删除。
所有写命令在 SQLite 写事务内复核动态权限；读取必须把权限过滤与数据投影保持在同一权威查询边界。
Direct 以永久 canonical pair key 为唯一真源，现有/软删除/并发都复用同一会话 ID。
Claude 仅作一次限时参考，不阻塞 Codex 主流程；绿色后按用户授权仅快进集成并推送，不触碰 main/Tag/Release/部署。
```

## 任务结果

### 修改摘要

- Shared 新增判别式创建请求、会话/完整列表、成员 upsert/列表 DTO，以及 `UserNotFound`、`ConversationTypeConflict` 稳定错误码；Web JSON 契约和错误码集合由测试固定。
- Server 新增六个认证会话端点、请求校验、命令与查询服务。所有频道/Direct/成员写命令在 SQLite 非 deferred Serializable 事务内重新确认 actor、目标、会话类型与当前角色；Direct 正反序、并发和软删恢复复用永久 pair key。
- `GET /api/conversations` 在单个数据库命令中返回 `Complete=true` 权威集合；详情和成员列表把可见性过滤与数据投影保持在同一 SQL。可组合 `ConversationAccessQuery` 为阶段 4 消息资源复用同一可见性真源；全局管理员可管理私有成员但未加入时不能读取私有会话，Public 访问隐式，Direct 成员不可变。
- 真实 HTTP/SQLite 测试覆盖动态降权、全局/本地管理员、成员并发 upsert/幂等删除、撤权后列表/详情/成员 403、类型冲突、Direct 双视角/并发/恢复、禁用目标、单查询计数、busy 503 与日志边界。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 Fast | Debug 0 警告、0 错误；116 项测试通过 |
| `未验证` | Claude challenge #21 | `ChallengeHead=22d60cf`；本机认证源优先级禁用 claude.ai connector，60 秒窗口内超时，无模型、workspace、费用或结论；按用户要求未重试 |
| `已验证` | 会话专项测试 | Server `FullyQualifiedName~Conversation` 共 `30/30` 通过；含真实 HTTP、SQLite 并发、软删恢复、撤权、单数据库命令计数、busy 503 与 Unicode/判别请求校验 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | format、Release 构建、134 项测试和 `git diff --check` 全部通过；Server 113、Shared 19、Client/Updater 各 1，构建 0 警告、0 错误 |
| `已验证` | `dotnet ef migrations has-pending-model-changes ...` | 返回“自上次 migration 后模型无变化” |
| `已验证` | `dotnet list RelayCove.sln package --vulnerable --include-transitive` | 8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 基准差异文件白名单 | `9c4f4d4..b9b0041` 共 26 个文件，0 个超出任务允许范围；`git diff --check` 通过 |
| `已验证` | Codex 固定差异复核 | 按 `REVIEW_TEMPLATE.md` 审查首个候选 `74eb8f6` 的授权、存在性泄漏、事务/并发、错误状态、日志和测试；发现后续消息资源缺少可组合共享访问查询，在 `b9b0041` 补齐；定向 30 项与 Full 134 项复测通过，最终 `ReviewHead=b9b004109183e0157bca5c16f0acdaf7a39c8940` 未发现剩余可操作问题 |
| `未验证` | Claude 候选 review | 用户要求 Claude 仅作参考且避免长耗时；前置 #21 已超时，故未重复候选调用，由 Codex 固定差异复核、真实 HTTP/SQLite 测试与 Full 降级覆盖 |

### 文件范围

- 新增：Shared 会话/成员 DTO；Server 会话端点、校验、命令/查询服务及结果类型；对应 Shared/Server 自动化测试；本任务文件。
- 修改：Conversation 更新时间方法、通用 SQLite busy 错误映射、Server 注册、稳定错误码及契约测试；工程方案、`DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md`。
- 删除：无。

### 决策与限制

- 决策：频道创建者为会话 Administrator；私有成员管理允许当前全局管理员或当前会话 Administrator，但全局管理覆盖不授予私有内容访问；权威列表是单查询完整集合；Direct 永久复用同一会话；详见 `DEC-009`。
- 已知限制：当前没有 Messages，因此列表消息字段和加入/重新加入水位只能为 0。阶段 4 必须在同一成员写事务中把水位初始化替换为当前 `MAX(Messages.Id)`；Public 普通用户当前没有显式成员行，其持久化已读/静音状态同样留给消息/read 切片冻结。
- 独立复核限制：Claude #21 和候选 review 均无结论，不能标记为通过；已由固定候选 Codex 复核、134 项 Full、专项并发/权限测试、model drift 与漏洞审计如实降级覆盖。

### 下一步

- 将完成提交仅快进合入并推送 `agent/v1-integration`；下一切片实现阶段 4 文字消息存储、HTTP 幂等发送、权限与 History/Sync 基础闭环，不提前实现 SignalR。
