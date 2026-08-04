# 阶段 10 权限化中文与附件名搜索 API

## 任务定义

- **任务名称：** 阶段 10 服务端权限化消息正文与附件原名搜索
- **状态：** `已完成，待仅快进合入`
- **基准提交：** `8ff8c15691e51bd5d33602fd16e5ef1227f6e9da`
- **工作分支：** `agent/stage-10-search-api`
- **生产代码提交：** `4c4edbab9fd8a3178d39cd7fbddd49c36c16c82c`
- **相关方案章节：** 1.5、4.1、11.2、12.8、15、阶段 10、21.4；`DEC-003/009/010/012/040/043`

### 目标

交付受 JWT 保护的 `GET /api/search`：可按中文或其他 Unicode 字面子串搜索当前有权查看的消息正文与已绑定附件原始文件名，支持可选会话范围、稳定限额和脱敏结果。该切片只冻结 Shared/Server 协议与真实 SQLite 行为，为下一切片的 WPF current/global 搜索和 Around 跳转提供后端。

### 已知事实

- `已验证`：绿色集成基线 `8ff8c15` 已包含 M2 全部附件闭环；其 production 代码通过最终 Fast/Full 1,348/1,348、两路 Codex 复核、真实 Windows 探针、model drift 与依赖漏洞审计。本切片尚未改动代码。
- `已验证`：当前内容权限唯一复用入口是 `ConversationAccessQuery.VisibleTo`：启用用户可见 Public，Private/Direct 只对当前成员可见；管理员身份不自动授予私有内容读取权。History/Around 与提及候选已有同构权限、403 和日志测试。
- `已验证`：`Message.Content` 最多 4,000 Unicode scalar 且可空；已绑定附件通过 `Message.Attachments` 关联，未绑定 upload lease 的 `Attachment.MessageId` 可空。一条消息最多 10 个附件。
- `已验证`：SQLite 已有 `(ConversationId, MessageId)` 与 `Attachments.OriginalFileName` 普通索引，但 `LIKE '%keyword%'` 不能依赖普通 B-tree 消除扫描；首版约 20 人规模不新增 Content 索引、migration、FTS/ICU 或第三方依赖。
- `已验证`：SQLite 官方契约规定默认 `LIKE` 只对 ASCII 字母大小写折叠，非 ASCII 大小写敏感；`ESCAPE` 可把 `%`、`_` 与 escape 字符本身作为字面量。当前 EF Core SQLite provider 将三参数 `EF.Functions.Like` 翻译为 `LIKE ... ESCAPE ...`。
- `已验证`：现有客户端 Around 链已经能按 `(conversationId, messageId)` 懒加载并滚动定位；客户端搜索 UI、迟到撤权门和一次性高亮留在下一切片。
- `已验证`：本决策唯一一次 Claude #79 按 Sonnet/XHigh 发起；当前 Desktop 任务仍只暴露旧 `consult_claude` 兼容入口，该入口再次强加 `$0.5` budget 并在答案前失败。无 job、正式答案、可靠实际模型、duration 或费用，按单次策略不重试；三路 Codex 只读调查与官方/本机证据继续作为裁定依据。

### 已裁定协议与查询边界

- `keyword` 为必填 query 参数：服务端先 trim，再验证 1–64 个有效 Unicode scalar；拒绝纯空白、无效 UTF-16 与 Unicode Control，不做 Unicode normalization。`%`、`_`、`\` 必须按字面量搜索；第一版只承诺中文/符号原码点子串与 SQLite 既有 ASCII case-fold，不承诺完整 Unicode case-fold。
- `conversationId` 可选；省略表示全局搜索，提供则表示单会话搜索。未知、删除、空 ID 或当前无权会话统一返回稳定 `ConversationAccessRevoked` 403；可见会话零命中返回 200 空集。`limit` 默认及最大均为 50，允许 1–50。
- 结果 SQL 必须从 `ConversationAccessQuery.VisibleTo` 导出消息，权限、正文/附件 `LIKE`、`Message.Id DESC` 排序与 `Take(limit + 1)` 在同一权限化查询中完成。附件匹配使用 `message.Attachments.Any(...)`，不得从 nullable/未绑定附件表驱动或在 join 后限流。
- 同一消息最多返回一项；多个匹配附件按 `Attachment.Id` 稳定选择一个 `MatchedAttachmentFileName`。正文和附件同时命中仍只返回一次。Direct `ConversationName` 与会话列表一致，派生为另一参与者的 `DisplayName`。
- `SearchResponse` 返回 `Results` 和 `HasMore`；每项包含方案规定的 message/conversation IDs、conversation/sender name、纯文本 `Snippet`、`CreatedAt` 与可空匹配附件名。snippet 围绕正文首次 SQLite-literal 等价命中生成，总长不超过 160 Unicode scalar且不切分 surrogate；附件-only 且正文为空时返回空字符串。所有 contract `ToString()` 对内容、显示名、文件名和 ID 脱敏。
- 搜索日志只记录 global/scoped、结果数、截断与拒绝状态，不记录 keyword、snippet、原文件名、会话/发送者显示名或任何内部 ID。
- 搜索按 JWT subject 独立采用固定窗口限流：每分钟 30 次、零排队；超限复用稳定的 `RateLimitExceeded` 429 响应，避免未命中 `%keyword%` 查询被单一账号持续放大。

### 范围

- 必须实现：
  - Shared search result/response contract 与脱敏 contract tests。
  - Server query validator、权限化 EF Core query service、endpoint/DI 映射。
  - 搜索账号级限流策略及稳定 429 回归测试。
  - 中文完整/部分词、正文/附件名、wildcard literal、去重、稳定顺序/限额/snippet、Public/Private/Direct/撤权/禁用/日志边界的真实 SQLite HTTP 测试。
- 允许修改：
  - `src/RelayCove.Shared/Messages/`
  - `src/RelayCove.Server/Endpoints/`、`Services/`、`RateLimiting/`、`Program.cs`
  - 对应 Shared/Server tests 与必要的 `docs/ai/` 记录。
- 明确不做：
  - Client transport/runtime/shell/WPF、结果点击、Around 导航或高亮。
  - 本地缓存搜索、自动搜索/typeahead、分页 cursor、相关性评分。
  - schema/migration、普通 Content 索引、FTS/ICU、外部搜索服务、新依赖或 VPS Gate。

### 验收标准

- [x] 认证用户可对全局或指定可见会话搜索中文完整词/中间部分词、消息正文和已绑定附件原名；响应字段、JSON 和 `ToString()` 契约稳定且脱敏。
- [x] 权限过滤在结果 SQL 内完成：Public 对启用用户可见，Private/Direct 只对当前成员可见，outsider admin 不例外；撤权/删除/未知 scoped 会话 403，全局零泄漏，可见零命中 200。
- [x] `%`、`_`、`\` 是字面量；同消息正文/多附件命中不重复，未绑定附件不出现；结果按 Message ID 降序，limit/HasMore 在唯一消息上计算。
- [x] keyword、limit、Unicode 与 snippet 边界 fail closed；emoji/代理项不被切断，Direct 会话名不为空，日志不暴露查询或结果内容/身份。
- [x] 搜索每账号每分钟最多 30 次且不排队；超限返回稳定 429，一个账号不会挤占另一个账号的配额。
- [x] Fast、最终 Full、model drift、依赖漏洞、空白检查与独立 Codex 复核通过；没有 migration、生产依赖或无关改动。

### 验证命令

```powershell
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Search"
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Search"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变既有 Public/Private/Direct 内容权限、附件绑定语义或已接受的消息不可变/ID 顺序，或者必须泄露未授权内容才能区分错误。
- 必须新增 migration、FTS/ICU、第三方依赖、外部搜索服务或不受限的查询/响应才能满足首版需求。
- EF/SQLite 无法在同一结果查询中表达权限、唯一消息限流和附件 `EXISTS`，且小型安全改写或受控参数化 SQL 仍无法形成可验证边界。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、方案 15/阶段 10/21.4、DEC-003/009/010/012/040/043、STATUS 和本任务。
只实现 Shared+Server 权限化 LIKE 搜索 API；不实现客户端，不新增 schema、FTS、依赖或搜索基础设施。
结果查询必须内嵌 ConversationAccessQuery.VisibleTo；附件用 message.Attachments.Any，先唯一消息再限流。
按字面量转义 LIKE；冻结 Unicode、snippet、Direct name、日志和 ToString 脱敏边界。
Claude #79 已失败且不重试；普通实现与审查使用 Codex reviewer 和本机真实 SQLite/HTTP 证据。
```

## 任务结果

`4c4edbab9fd8a3178d39cd7fbddd49c36c16c82c` 完成 Shared 搜索响应、服务端验证、权限化查询、Unicode-safe snippet、JWT endpoint、DI 与每 subject 30 次/分钟零排队限流；没有 migration、FTS/ICU、生产依赖或客户端改动。

- `已验证`：Search 定向真实 HTTP/SQLite 与 contract 测试 30/30（Shared 2、Server 28）。覆盖中文部分匹配、正文/附件、未绑定附件排除、多附件去重、Public/Private/Direct/管理员不旁路、撤权/删除/禁用、限额/顺序、ASCII `LIKE` 大小写折叠与非 ASCII 精确大小写、snippet 定位、日志/`ToString()` 脱敏和跨 subject 429 隔离。
- `已验证`：Fast 与最终 Full 均为 1,378/1,378（Shared 41、Server 283、Client 1,053、Updater 1）；Debug/Release 均 0 警告、0 错误，format 与 `git diff --check` 通过。
- `已验证`：EF model 无待迁移变化；八个项目的直接与传递包未发现已知漏洞。
- `已验证`：安全复核先发现未命中扫描缺少 subject 限流，协议复核先发现 SQLite 大小写语义只由手写 matcher 覆盖；两项均修复并补真实回归，原审查者复审均 `PASS`、无剩余 P0–P2。
- `已记录`：Claude #79 在答案前失败且无正式结论，按单次策略没有重试；未把失败冒充审查通过。
- 已知限制：第一版是小规模有界 `LIKE '%keyword%'`，不提供 FTS/ICU、相关性、cursor、自动 typeahead 或客户端界面；后者进入下一切片。
- 下一步：仅快进合入 `agent/v1-integration`，立即实现客户端 current/global 搜索、结果列表、Around 跳转与一次性高亮。
