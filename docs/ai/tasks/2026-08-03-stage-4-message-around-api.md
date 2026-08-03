# 阶段 4：消息 around API

## 状态

- `completed`
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

- [x] around 请求与响应契约、默认值、范围和脱敏字符串稳定。
- [x] 当前有权用户取得真实目标及其最近前后上下文，消息严格升序、目标恰好一次、mentions 不丢失，两侧更多标志准确。
- [x] 空前文/空后文和零条侧窗口有明确结果；非法范围、非正目标、跨会话/不存在目标返回稳定错误。
- [x] 未认证/禁用用户遵循既有 401；未知/删除/不可访问/撤权会话统一 403，且权限先于目标信息暴露。
- [x] Fast/Full、model drift、漏洞审计、文件白名单、空白和固定差异复核通过。

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

- 新增 Shared `MessageAroundResponse`，固定 `messages/targetMessageId/hasMoreBefore/hasMoreAfter` Web JSON 顺序并隐藏消息集合与目标 ID 的字符串表示。
- 新增 around 参数校验和 HTTP endpoint；before/after 默认 20、各允许 0..100，复用稳定认证、验证、撤权和目标错误 envelope。
- `MessageQueryService` 先用当前权限确认会话与目标归属，再用一个真实 SQLite 有限 `UNION ALL` 投影取得前侧 `N+1`、目标、后侧 `N+1` 及完整 mentions；最终再次绑定权限并按 ID 升序组装响应。
- 将 read 专用内部目标状态泛化为 `MessageTargetInvalid`，保持 read 对外错误不变；全零 GUID 与其他未知会话一致 fail-closed 为 403。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `98f4925` 为刚通过 Full、167 项测试、model drift 与漏洞审计的 read-through 集成头 |
| `已验证` | around 契约/验证定向测试 | Shared JSON/脱敏 2 项通过；Server around/validator 16 项通过 |
| `已验证` | 真实 HTTP/SQLite around 场景 | 默认/有限/零窗口、首尾空侧、严格升序、目标单例、双侧更多标志、3 mentions 不扩窗、Public/Private/Direct、全局管理员不越权、目标/会话边界、撤权/删除/禁用与日志脱敏通过 |
| `已验证` | 有限查询形状 | 服务调用固定执行 2 条 SELECT：首条授权化目标确认，第二条有限双侧投影；真实 SQLite 已翻译并返回正确最近窗口 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 0 警告、0 错误；Server 152、Shared 27、Client 1、Updater 1，共 181 项测试通过 |
| `已修复` | 首轮 Full | formatter 仅发现 Windows 工作副本 CRLF 与仓库 LF 的机械差异；规范化后没有实际代码差异或行为失败 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Full` | format clean；Release 0 警告、0 错误；181 项测试通过；`git diff --check` 通过 |
| `已验证` | EF model drift | `has-pending-model-changes` 返回自上次 migration 后模型无变化 |
| `已验证` | 漏洞审计 | 8 个源/测试项目均无已知易受攻击的直接或传递包 |
| `未验证` | Claude XHigh challenge #24 | 60 秒内因认证源优先级超时，无模型、workspace、费用或结论；按用户要求不重试、不阻塞 |
| `已验证` | Codex 固定差异复核 | `ReviewBase=acbd34b`、`ReviewHead=6189e7f`；协议、双侧窗口、授权优先/最终重检、目标归属、mentions、错误/日志、取消、文件白名单和空白检查无剩余发现 |

### 文件范围

- 新增：Shared around 响应；Server around HTTP/SQLite 测试；Shared 契约测试。
- 修改：工程方案、决策/状态/执行/任务文档；消息 endpoint、查询、验证、目标状态及 read 内部状态名；validator 测试。
- 删除：无。

### 决策与限制

- 决策：`DEC-012`。around 使用专用双侧响应，窗口各 0..100；只有已获内容访问的会话才区分坏目标为 400，最终投影缺少不可变目标时按撤权 403 fail-closed。
- 已知限制：本切片不实现 Search、固定上界 Sync、SignalR、客户端跳转/高亮、本地缓存或附件；两次只读命令之间新增消息可进入最终 around 窗口，但目标与现有消息不可变且最终权限重新检查。
- Claude 未返回第二意见；最终结论由 Codex 结合仓库协议/模型证据、真实 HTTP/SQLite 自动化与固定差异复核独立承担。

### 下一步

- 仅快进 `agent/v1-integration` 并推送，随后开始固定上界 `GET /api/sync` 服务端纵向切片。
