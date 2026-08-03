# 阶段 8 会话作用域提及候选

## 任务定义

- **任务名称：** 阶段 8 `@用户` 会话作用域候选协议与授权查询
- **状态：** `进行中`
- **基准提交：** `924ac878790a534218ef72b717935c047e59fcb9`
- **工作分支：** `agent/stage-8-mention-candidates`
- **相关方案章节：** 8.3、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-009`、`DEC-010`、`DEC-035`

### 目标

为普通客户端提供一个有界、会话作用域、与真实消息发送提及授权完全一致的候选查询：Public 只返回当前活跃用户，Private/Direct 只返回当前活跃成员。接口不复用管理员响应，不暴露密码、管理员/禁用状态、在线时间或全局无界用户目录。

### 已知事实

- `已验证`：绿色集成头 `924ac87` 的 Fast 基线为 788/788；当前分支建立时工作树干净。
- `已验证`：Shared/Server 已冻结 `MentionUserIds` 最多 20 个非空唯一 ID；服务端发送在同一写事务中要求 Public 目标为活跃用户、Private/Direct 目标为活跃成员。
- `已验证`：普通用户当前只能列出 Private/Direct 成员；Public `/members` 按冻结契约稳定返回类型冲突，管理员用户响应包含本任务不应暴露的字段。
- `已验证`：用户名是 3–64 位 ASCII `[A-Za-z0-9._-]`，规范身份键为 invariant-uppercase `NormalizedUserName`；昵称不是唯一身份 token。
- `已验证`：Claude #67 MCP 只读协议/安全 challenge 最终因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责威胁建模、实现和本机验证。

### 假设

- `假设`：候选查询只接受 1–64 位用户名字符前缀，默认 20、允许 1–50；按规范用户名和用户 ID 稳定排序，读取 `limit+1` 产生 `HasMore`，客户端不需要游标。
- `假设`：返回 `UserId`、`UserName`、`DisplayName` 足以让后续客户端插入唯一 `@UserName` token 并展示昵称；头像留到附件切片。
- `假设`：允许候选包含当前用户，因为发送端现有“当前可访问正常用户”契约允许自提及；客户端可自行降低其排序或不展示，但服务端不制造不一致授权。

### 范围

- 必须实现：
  - 新增脱敏 `MentionCandidateDto` / `MentionCandidateListResponse` Shared 契约。
  - 新增 `GET /api/conversations/{conversationId}/mention-candidates?query=...&limit=...`；query 必填且按用户名字符精确校验，limit 有界。
  - 候选 SQL 自身同时绑定活跃 actor、未删除可访问会话、活跃 candidate 与会话类型规则；Public 为所有活跃用户，Private/Direct 为当前成员。
  - 用户名前缀匹配对 `_` 等合法字符按字面量处理；结果按规范用户名/ID 稳定排序、去重、最多 `limit`，并准确返回 `HasMore`。
  - 无候选时只做最小当前访问复核以区分授权空 200 与撤权 403；禁用 actor/候选、未知/删除/撤权会话、无认证、非法 query/limit 与 busy 有稳定结果。
  - 日志、错误和 `ToString()` 不包含 query、用户名、昵称或候选 ID。
- 允许修改：
  - Shared Messages 契约、Server conversation endpoint/query/validator/DI 与 Shared/Server 测试；必要的 `docs/ai/` 记录。
- 明确不做：
  - 客户端候选 UI/传输、发送非空 MentionUserIds、全局用户目录、昵称搜索、模糊/全文搜索、头像、在线状态、管理员字段、schema/migration、新依赖或 VPS。

### 验收标准

- [ ] Shared 响应只含会话 ID、候选身份三字段、`HasMore`，所有 `ToString()` 脱敏。
- [ ] Public/Private/Direct 结果与消息发送的 `AreMentionsAccessibleAsync` 规则一致；outsider、撤权、禁用、删除与未知会话 fail-closed。
- [ ] 前缀/limit 校验、大小写、字面 `_`、稳定排序、limit+1/HasMore、空结果和无敏感日志有真实 HTTP/SQLite 证据。
- [ ] Fast/Full、Shared/validator/service/endpoint 定向与重复、model drift、八项目漏洞审计和空白检查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~MentionCandidate"
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~MentionCandidate"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要暴露管理员/禁用/在线字段、开放无界全局目录、改变 Public `/members` 契约、修改发送提及授权或引入 schema/依赖。
- 无法让候选查询自身绑定当前 actor/会话访问，或 SQLite 无法对合法用户名字符实现确定的字面前缀匹配。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 8.3/10.4/12.1–12.3/12.6–12.8/阶段 8、docs/ai/STATUS.md 和本任务。
只实现会话作用域提及候选协议/服务端查询；不实现客户端或全局用户目录。
查询授权必须与 MessageCommandService 的实际提及授权一致，候选 SQL 自身绑定 actor 与会话。
query、用户名、昵称、ID 不进入日志、错误和 ToString；无候选与无权限必须稳定区分。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 788/788；Shared 35、Server 175、Client 577、Updater 1。 |
| `未验证` | 实现与最终门禁 | 任务进行中。 |

### 文件范围

- 新增：本任务记录。
- 修改：待完成。
- 删除：无。

### 决策与限制

- 决策：待完成。
- 已知限制：只支持规范用户名前缀，不支持昵称/模糊搜索；客户端闭环留到下一切片。

### 下一步

- 实现并验证 Shared 契约、服务端授权查询和 HTTP endpoint。
