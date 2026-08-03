# 阶段 4：消息 read-through API

## 状态

- `completed`
- 分支：`agent/stage-4-message-read-api`
- 基线：`1a957784f86f09f22d1ea8bd6edbfa382d7e9f7e`

## 目标

实现 `POST /api/conversations/{conversationId}/read` 的最小服务端纵向闭环：当前有权用户只能用该会话内真实存在的消息推进已读边界，服务端确认值单调不回退，并让权威会话 DTO 的未读数立即反映该状态。

## 已有证据与待冻结边界

- `已验证`：`DEC-003` 和工程方案要求目标消息真实属于该会话、保存 `MAX(old, requested)`、拒绝任意极大 ID；客户端只有在服务端确认值不小于 pending 目标时才能清除 pending。
- `已验证`：Private/Direct 的内容访问要求当前成员；Public 对所有正常用户隐式可见，但当前并不为每位 Public 读者预建 `ConversationMembers` 行。Public 的个人 read-through 因而需要在首次成功读回执时创建仅用于个人状态的内部成员行，成员管理/list API 仍不得把 Public 暴露成成员制频道。
- `已验证`：ConversationMember 实体已提供单调 `AdvanceLastReadMessageId`；会话查询从当前 actor 的成员行读取水位，没有行时为 0，并只统计他人且大于该水位的消息。
- `已验证`：会话/成员写操作与消息发送均使用 SQLite 非 deferred Serializable 写事务；read-through 必须沿用同一动态授权和串行写边界，避免撤权与推进交错。
- `已验证`：Claude XHigh challenge #23 在 60 秒内因本机认证源优先级禁用 claude.ai connector 而超时，没有返回模型、workspace、费用或结论；按用户要求不重试、不阻塞 Codex，`DEC-011` 由仓库事务/授权/模型证据独立收敛。

## 范围

- Shared：新增 read 请求与确认 DTO，固定 JSON 和脱敏字符串边界。
- Server：新增 read endpoint、请求验证和事务服务；动态检查当前用户/会话/目标消息。
- Private/Direct：只更新现有成员；撤权、未知或软删会话统一 `403 ConversationAccessRevoked`。
- Public：正常用户首次成功上报时创建内部 `ConversationMemberRole.Member` 状态行；重复/较小上报不回退；不改变 Public 成员管理接口的 `409 ConversationTypeConflict`。
- 返回 200 确认 `ConversationId + LastReadMessageId`；无效 ID/跨会话消息返回稳定 400；SQLite busy 继续由统一 middleware 返回 503。
- 补齐实体、服务、HTTP、并发、撤权、Public 首次状态、私有单调推进、跨会话拒绝、未读聚合和日志测试。

## 非目标

- 不实现 around、Search、`GET /api/sync`、SignalR read receipt、客户端 `PendingReadThroughMessageId`、本地 SQLite 或通知。
- 不增加新 migration、新依赖、Public 成员列表能力、消息编辑/删除或任意最大 ID 快进。

## 验收标准

- [x] 请求/确认契约 JSON 稳定且日志字符串不泄漏非必要状态。
- [x] 当前有权用户用同会话真实消息推进水位并收到 200 确认；重复、较小及并发上报最终为最大真实目标且不回退。
- [x] Public 普通用户可持久化个人水位且不会开放 Public 成员管理/list；Private/Direct 仅现有成员可推进。
- [x] 任意极大 ID、跨会话 ID、空/非正 ID 稳定 400；未知/删除/撤权稳定 403，禁用用户遵循既有动态 JWT 校验稳定 401，且权限检查先于目标消息信息暴露。
- [x] 会话列表的 `LastReadMessageId` 与 `UnreadCount` 在成功上报后正确，自己的消息不计未读。
- [x] busy 503、日志无消息正文/昵称、Fast/Full、model drift、漏洞审计、白名单、空白和固定差异复核通过。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 证据要求 Public 不得用 ConversationMembers 保存个人状态，或必须改变 Public 可见性/成员 API 协议。
- 需要增加消息删除、全局管理员内容读取、跨服务端写实例或客户端本地状态。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增 Shared read-through 请求与确认 DTO，并固定 Web JSON 字段和脱敏字符串表示。
- 新增 `POST /api/conversations/{conversationId}/read`、请求校验及 Serializable 事务服务：先动态复核内容访问，再确认目标消息属于当前会话，最后单调推进并返回服务端确认水位。
- Private/Direct 只更新既有成员；Public 普通用户首次有效上报创建仅承载个人水位的内部状态行，不修改会话更新时间。
- 加固 Public 成员 list/upsert/remove 的类型短路，在展开成员集合前返回 `ConversationTypeConflict`，避免个人状态行被成员 API 查询放大；既有 Private 全局管理员覆盖和 Direct/Private 授权保持不变。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 Fast | Debug 0 警告、0 错误；Server 131、Shared 23、Client 1、Updater 1，共 156 项测试通过 |
| `已验证` | read-through 定向与并发回归 | Public 首次并发建行、重复/较小/较大单调推进、Private 并发最大值、Direct、撤权、目标/权限边界、busy 与契约测试通过；Public 首次并发场景连续 5 次通过 |
| `已修复` | Public 状态隔离复核 | 首轮查询改写触发 SQLite 不支持 APPLY，未进入提交；改为授权化类型预查后，Conversation/MessageRead 定向测试 13 项通过，并保留全局管理员读取 Private 成员清单语义 |
| `已验证` | Public 成员 API 隔离 | 已存在个人水位行时，成员 list/write 仍为 `ConversationTypeConflict`；数据库命令断言确认不会 JOIN `ConversationMembers` 展开状态行 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 0 警告、0 错误；Server 140、Shared 25、Client 1、Updater 1，共 167 项测试通过 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Full` | format clean；Release 0 警告、0 错误；167 项测试通过；`git diff --check` 通过 |
| `已验证` | EF model drift | `has-pending-model-changes` 返回自上次 migration 后模型无变化 |
| `已验证` | 漏洞审计 | 8 个源/测试项目均无已知易受攻击的直接或传递包 |
| `未验证` | Claude XHigh challenge #23 | 60 秒内因认证源优先级超时，无模型、workspace、费用或结论；按用户要求不重试、不阻塞 |
| `已验证` | Codex 固定差异复核 | `ReviewBase=9c8211f`、`ReviewHead=d92552a`；契约、授权优先、目标归属、单调/并发、Public 隔离、日志、错误映射、文件白名单和空白检查无剩余发现 |

### 文件范围

- 新增：Shared read 请求/确认；Server read-through 服务；Server/Shared read 契约、验证、HTTP 与并发测试。
- 修改：工程方案、决策/状态/执行/任务文档；消息 endpoint、验证、状态与 DI；Public 成员查询/命令隔离。
- 删除：无。

### 决策与限制

- 决策：`DEC-011`。只接受当前会话内真实消息；权限判断先于目标查询；服务端确认值单调；Public 个人状态复用不公开的成员行且不改变 Public 的成员 API 协议。
- 已知限制：本切片不实现 around、固定上界 Sync、Search、SignalR read receipt、客户端 pending 水位或本地缓存，也不支持跨服务端写实例。
- Claude 未返回第二意见；最终结论由 Codex 结合仓库事务/授权证据、真实 HTTP/SQLite 自动化和固定差异复核独立承担。

### 下一步

- 仅快进 `agent/v1-integration` 并推送，随后开始阶段 4 around 与固定上界 Sync 的下一个纵向任务。
