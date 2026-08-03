# 阶段 4：消息 around API

## 状态

- `in_progress`
- 分支：`agent/stage-4-message-around-api`
- 基线：`98f4925f86697d9b86b26c5e9b9b69753b6c84b1`

## 目标

实现 `GET /api/conversations/{conversationId}/messages/around/{messageId}` 的最小服务端纵向闭环：当前有内容访问权的用户可按真实目标消息取得其前后有限上下文，结果严格按消息 ID 升序且不会通过目标或会话状态泄露不可访问资源。

## 已有证据与待冻结边界

- `已验证`：工程方案阶段 4 与搜索跳转流程要求 `messages/around/{messageId}?before=20&after=20`；当前尚无该 endpoint、Shared 响应或验证规则。
- `已验证`：Message/MessageMention 不可变且 committed ID 使用 SQLite AUTOINCREMENT；History 已提供权限化 MessageDto 投影、mention 聚合和唯一 ID keyset，可复用字段语义但不能冒充 around 响应。
- `已验证`：Public 对正常用户隐式可见，Private/Direct 只对当前成员提供内容；全局管理员的成员管理覆盖不授予私有内容读取权。未知、删除、不可访问会话统一 403，权限判断必须先于目标信息暴露。
- `已验证`：`DEC-012` 冻结 `MessageAroundResponse(Messages, TargetMessageId, HasMoreBefore, HasMoreAfter)`；before/after 默认 20、各为 `0..100`，结果包含目标恰好一次、取最近双侧上下文并严格升序，零窗口仍报告对应更多标志。
- `已验证`：非正目标/窗口越界稳定 400；先用当前内容权限确认会话，再区分已获访问会话内的不存在/跨会话目标为 400；最终有限投影再次绑定权限且缺少目标时按撤权 fail-closed 为 403。
- `已验证`：Claude XHigh challenge #24 在 60 秒内因本机认证源优先级禁用 claude.ai connector 而超时，没有返回模型、workspace、费用或结论；按用户要求不重试、不阻塞 Codex，`DEC-012` 由仓库协议/授权/不可变模型证据独立收敛。

## 范围

- Shared：新增专用 around 响应 DTO，固定 JSON 与敏感消息集合的字符串脱敏边界。
- Server：新增 around 查询参数验证、endpoint 和只读查询服务；复用当前内容访问语义与 MessageDto 形状。
- 返回目标消息、最多 `before` 条最近前文和最多 `after` 条最近后文，最终按 ID 严格升序，并明确两侧是否还有更多消息。
- 补齐契约、验证、HTTP、权限/撤权、目标归属、边界、mentions、空侧、顺序、日志与数据库命令测试。

## 非目标

- 不实现 Search、固定上界 Sync、SignalR、客户端滚动/高亮、本地 SQLite、附件或消息编辑删除。
- 不新增 migration、依赖、游标持久化或跨服务端写实例协议。

## 验收标准

- [ ] around 请求与响应契约、默认值、范围和脱敏字符串稳定。
- [ ] 当前有权用户取得真实目标及其最近前后上下文，消息严格升序、目标恰好一次、mentions 不丢失，两侧更多标志准确。
- [ ] 空前文/空后文和零条侧窗口有明确结果；非法范围、非正目标、跨会话/不存在目标返回稳定错误。
- [ ] 未认证/禁用用户遵循既有 401；未知/删除/不可访问/撤权会话统一 403，且权限先于目标信息暴露。
- [ ] Fast/Full、model drift、漏洞审计、文件白名单、空白和固定差异复核通过。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 证据要求改变既有内容访问规则、MessageDto 不可变字段或消息 ID/提交顺序。
- 需要增加新 migration、大型依赖、跨服务端快照协议或 Search 客户端行为。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `98f4925` 为刚通过 Full、167 项测试、model drift 与漏洞审计的 read-through 集成头 |

### 下一步

- 按 `DEC-012` 实现 Shared 契约、授权化有限查询、endpoint 与自动化验证。
