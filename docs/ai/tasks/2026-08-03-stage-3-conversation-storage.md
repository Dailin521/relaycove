# 阶段 3：会话与成员持久化

## 任务定义

- **任务名称：** 阶段 3 — Conversations/ConversationMembers 最小持久化与迁移
- **状态：** 已完成
- **基准提交：** `db92a5b0ac307d6d221a5880d64eee87082b348e`
- **工作分支：** `agent/stage-3-conversation-storage`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.2、7.3、10.1、10.2、11.1、11.2、阶段 3；`DEC-003`、`DEC-005`

### 目标

建立公共频道、私有频道和一对一私聊后续服务可直接使用的最小服务端存储模型。真实 SQLite migration 必须固定会话类型、成员角色、外键、唯一性、UTC/GUID、软删除和单调已读边界，且不提前实现 HTTP 或 SignalR 行为。

### 已知事实

- `已验证`：基准 `db92a5b` 的 Fast 通过，Debug 构建 0 警告、0 错误；Server 83、Shared 15、Client/Updater 各 1，共 100 项测试通过，工作树干净。
- `已验证`：工程方案明确 Conversations 的 7 个字段、ConversationMembers 的复合主键及 6 个字段，并要求 `Conversations.Type`、`ConversationMembers.UserId` 索引；`ConversationType` 值固定为 PublicChannel=1、PrivateChannel=2、Direct=3。
- `已验证`：`DEC-003` 要求 `LastReadMessageId` 只表示单调已读边界，加入或重新加入时由事务初始化为当前会话最大消息 ID；当前成员可懒加载全部历史。Messages 表属于阶段 4，本切片不存在可供该字段引用的外键。
- `已验证`：现有存储将 GUID 保存为非空小写标准 D 文本、时间保存为毫秒 UTC 文本，并通过实体、EF converter、SQLite CHECK 和真实 migration 测试共同防守；应用启动不自动迁移。
- `已验证`：公共频道对所有正常用户可见，私有频道只对成员可见，Direct 只对两名参与者可见；这些查询/授权行为属于后续阶段 3 API 切片。
- `已验证`：[EF Core SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations) 建议用 UTC `DateTime` 替代不可原生排序的 `DateTimeOffset`，并列明 CreateTable/CreateIndex 与当前 migration 操作受支持；[EF Core cascade delete](https://learn.microsoft.com/en-us/ef/core/saving/cascade-delete) 明确必填关系默认级联、`Restrict` 生成数据库限制，且警告软删除模型不得让普通删除路径触发数据库级联。
- `已验证`：[EF Core indexes](https://learn.microsoft.com/en-us/ef/core/modeling/indexes) 与 [keys](https://learn.microsoft.com/en-us/ef/core/modeling/keys) 支持唯一索引及复合主键；本切片仍以真实 SQLite migration 和约束冲突测试作为最终证据。
- `未验证`：Claude XHigh challenge #20 对固定 `ChallengeHead=cd90023` 在 120 秒内因本机认证源优先级禁用 claude.ai connector 而失败，无模型、workspace、费用或结论；按用户要求不重试，Codex 依据仓库与 Microsoft 官方证据收敛 `DEC-008`。

### 假设

- `假设`：成员角色只冻结 `Member=1` 与 `Administrator=2`；全局 `User.IsAdmin` 与频道内角色相互独立。Direct 成员只能是 Member，此跨表不变量由后续创建服务事务保证。
- `假设`：频道名称为 1–100 个 Unicode scalar value；Direct 的展示名未来按当前用户动态生成，数据库 `Name` 保存空字符串。为使一对一创建在并发下可证明单例，Conversations 增加由两个小写排序 GUID 组成的可空 `DirectParticipantKey`，仅 Direct 必填并建立覆盖软删除行的永久唯一索引；重新发起时恢复原线程。
- `假设`：会话以 `IsDeleted` 软删除；硬删除会话级联成员。删除用户级联其成员行，但创建者外键使用 Restrict，避免删除创建者时静默删除整段会话历史；阶段 11 用户删除必须显式处理创建者引用。
- `假设`：`LastReadMessageId` 为非负 `long`，实体只能单调推进；新成员构造器接收由调用事务计算的初始值，空消息会话使用 0。本切片不查询不存在的 Messages 表。

### 范围

- 必须实现：
  - Shared `ConversationType` 与 `ConversationMemberRole` 固定数值枚举。
  - Server `Conversation` / `ConversationMember` 实体及名称、类型、角色、Direct key、UTC、非负/单调已读和软删除不变量。
  - DbContext 映射、SQLite CHECK、复合主键、类型/成员/参与者唯一索引及明确外键删除行为。
  - 新增 migration、model snapshot 与真实 SQLite up/down、model drift、约束、索引、级联/限制、round-trip 测试。
  - 新增决策记录，冻结本切片会影响后续 API/迁移的数据库语义。
- 允许修改：
  - `src/RelayCove.Shared/**`
  - `src/RelayCove.Server/Data/**`
  - `tests/RelayCove.Shared.Tests/**`
  - `tests/RelayCove.Server.Tests/Data/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - 本任务文件
- 明确不做：
  - 不实现会话/成员 HTTP API、DTO、权限 handler、管理员业务服务或稳定错误响应；下一阶段 3 切片完成。
  - 不创建 Messages/MessageMentions，不查询消息最大 ID，不实现 History/Sync/read/未读计数；阶段 4 处理。
  - 不实现 SignalR、客户端本地表/UI、附件外键或物理清理已软删除会话。
  - 不自动运行 migration，不引入数据库或 EF 之外的新依赖，不改变单进程单 SQLite 写实例边界。

### 验收标准

- [x] 新 migration 在真实 SQLite 上创建字段、CHECK、索引和外键，能回滚且 `HasPendingModelChanges()` 为 false。
- [x] Conversation 三种类型、频道名称、Direct 参与者键、角色、非空 GUID、毫秒 UTC、布尔值及非负已读边界同时受领域与数据库约束保护。
- [x] ConversationMembers 使用 `(ConversationId, UserId)` 复合主键；重复成员、非法外键/枚举/已读值失败，按约定验证会话硬删除级联、用户成员级联和创建者删除限制。
- [x] 两个参与者无序等价的 Direct key 在并发持久化边界永久只能存在一个会话（含软删除行）；非 Direct 不得携带该键。
- [x] 已读边界只允许单调推进；Direct `Name`/频道名称及软删除 `UpdatedAt` round-trip 行为有自动化测试。
- [x] Fast、Full、漏洞审计、文件白名单、`git diff --check` 与固定差异独立复核通过或按规则如实记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check 'db92a5b0ac307d6d221a5880d64eee87082b348e..HEAD'
```

### 停止并询问

- 证据要求改变三种会话类型、`DEC-003` 已读/重入语义、单 SQLite 写实例、应用不自动迁移或当前认证身份键。
- 必须引入新的基础设施/大型依赖，或无法在不创建 Messages 表的前提下建立后续可兼容的成员已读字段。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现 Conversations/ConversationMembers 存储与 migration，不提前实现会话 API、消息或 SignalR。
复用既有小写 GUID、毫秒 UTC、显式 migration 与真实 SQLite 约束测试口径。
数据库/协议决定先以仓库和官方证据收敛；Claude 仅作一次限时参考，不得阻塞 Codex 主流程。
Fast 后形成代码检查点，Full 后固定 ReviewHead；绿色后按用户授权仅快进集成并推送，不触碰 main/Tag/Release/部署。
```

## 任务结果

### 修改摘要

- 新增 Shared `ConversationType` / `ConversationMemberRole` 固定数值枚举，以及 Server `Conversation` / `ConversationMember` 实体；频道名称按 Unicode scalar 校验，Direct 使用无序等价 canonical pair key，已读边界只能单调推进，会话更新采用毫秒 UTC。
- 新增 Conversations/ConversationMembers EF 映射与 `AddConversationStorage` migration：类型/名称/Direct key、GUID、时间、布尔、角色和非负水位均有 SQLite CHECK；复合主键、类型/用户/创建者/永久 Direct 唯一索引及 Restrict/Cascade 外键明确落库。
- 工程方案与 `DEC-008` 冻结 Direct 动态名称、软删除后恢复同一线程、成员角色、创建者限制删除和跨表服务事务边界；未提前实现 API、Messages 或 SignalR。
- 真实 SQLite 测试覆盖 migration up/down/model drift、旧认证数据升级/降级保留、round-trip、NULL 三值逻辑、非法枚举/布尔/UTC/外键/复合主键、永久 Direct 唯一和删除行为。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 Fast | Debug 0 警告、0 错误；100 项测试通过 |
| `未验证` | Claude challenge #20 | `ChallengeHead=cd90023`；本机认证源优先级禁用 claude.ai connector，120 秒窗口内失败，无模型、workspace、费用或结论；按用户要求未重试 |
| `已验证` | Server 定向测试 | `98/98` 通过；其中 DbContext migration/约束定向 `10/10`，含旧 Users/RefreshTokens 在升级与降级中的保留 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 构建 0 警告、0 错误；Server 98、Shared 16、Client/Updater 各 1，共 116 项测试通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | format、Release 构建、116 项测试和 `git diff --check` 全部通过；构建 0 警告、0 错误 |
| `已验证` | `dotnet ef migrations has-pending-model-changes ...` | 构建成功并返回“自上次 migration 后模型无变化” |
| `已验证` | `dotnet list RelayCove.sln package --vulnerable --include-transitive` | 8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 基准差异文件白名单 | `cd90023..1a3c492` 共 17 个文件，0 个超出任务允许范围；`git diff --check` 通过 |
| `已验证` | Codex 固定差异复核 | 按 `REVIEW_TEMPLATE.md` 审查 `cd90023..1a3c492`；发现旧认证数据升级/降级保留证据缺口并在 `1a3c492` 补齐，复核后未发现剩余可操作问题 |
| `未验证` | Claude 候选 review | 用户要求 Claude 仅作参考且避免长耗时；前置 #20 已因 connector 配置失败，故未重复候选调用，由 Codex 固定差异复核、真实 migration 测试与 Full 降级覆盖 |

### 文件范围

- 新增：Shared 会话枚举；Server 会话/成员实体及 migration；对应 Shared/Server 测试；本任务文件。
- 修改：Server User 导航与 DbContext/model snapshot；工程方案、`DECISIONS.md`、`STATUS.md` 与 `V1_EXECUTION.md`。
- 删除：无。

### 决策与限制

- 决策：Direct canonical pair key 对软删除行永久唯一，重新发起时恢复同一会话；Direct 名称按请求用户动态派生；创建者硬删除 Restrict，成员随会话或成员用户硬删除级联；详见 `DEC-008`。
- 已知限制：Direct 恰好两名 Member、成员与 key 对应、加入/重新加入时读取当前 Messages 最大 ID 是跨表不变量，留给下一阶段 3 服务事务；当前 schema 尚无 Messages，初始水位只能是 0。频道名控制字符/Unicode 空白由唯一写入实体完整防守，SQLite CHECK 负责类型对应、1–100 长度和非空 ASCII trim，不声称数据库内建完整 Unicode 分类器。
- 独立复核限制：Claude #20 和候选 review 均无结论，不能标记为通过；已由固定候选 Codex 复核、116 项测试、Full、model drift 和漏洞审计如实降级覆盖。

### 下一步

- 将完成提交仅快进合入 `agent/v1-integration`；下一切片实现阶段 3 会话创建、成员管理、权威列表、Direct 获取/恢复和动态访问校验。
