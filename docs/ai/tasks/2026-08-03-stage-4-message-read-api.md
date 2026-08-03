# 阶段 4：消息 read-through API

## 状态

- `in_progress`
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

- [ ] 请求/确认契约 JSON 稳定且日志字符串不泄漏非必要状态。
- [ ] 当前有权用户用同会话真实消息推进水位并收到 200 确认；重复、较小及并发上报最终为最大真实目标且不回退。
- [ ] Public 普通用户可持久化个人水位且不会开放 Public 成员管理/list；Private/Direct 仅现有成员可推进。
- [ ] 任意极大 ID、跨会话 ID、空/非正 ID 稳定 400；未知/删除/撤权稳定 403，禁用用户遵循既有动态 JWT 校验稳定 401，且权限检查先于目标消息信息暴露。
- [ ] 会话列表的 `LastReadMessageId` 与 `UnreadCount` 在成功上报后正确，自己的消息不计未读。
- [ ] busy 503、日志无消息正文/昵称、Fast/Full、model drift、漏洞审计、白名单、空白和固定差异复核通过。

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

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 Fast | Debug 0 警告、0 错误；Server 131、Shared 23、Client 1、Updater 1，共 156 项测试通过 |

### 下一步

- 按 `DEC-011` 实现 Shared 契约、事务服务、endpoint 与自动化验证。
