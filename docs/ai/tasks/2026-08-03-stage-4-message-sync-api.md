# 阶段 4：固定上界 Sync API

## 状态

- `in_progress`
- 分支：`agent/stage-4-message-sync-api`
- 基线：`9500836415913fd89b1b10f4babff7f821efec34`

## 目标

按已接受的 `DEC-003` 实现 `GET /api/sync` 服务端纵向闭环：用全局消息 ID 的固定 `SnapshotUpperBound` 分页，在每一页按当前权限重新过滤，正确跨过无权限空洞，并返回客户端可验证且必然前进的权威游标。

## 已冻结证据

- `已验证`：规范已固定 `SyncResponse(Messages, NextCursor, SnapshotUpperBound, HasMore)`；首页省略上界并在同一只读数据库快照读取当前 `MAX(Messages.Id)` 与第一页，续页原样携带上界。
- `已验证`：候选满足 `cursor < Id <= SnapshotUpperBound` 并按 ID 升序取 `limit+1`；有更多时返回前 limit 条且 NextCursor 为页尾 ID，否则 NextCursor 直接推进到快照上界，包括空页和末尾无权限空洞。
- `已验证`：每页重新应用当前内容权限。Public 对正常用户可见；Private/Direct 要求当前成员；Private 还排除 `Id <= actor.LastReadMessageId` 的加入前/已读历史，Direct 与 Public 不使用该过滤。
- `已验证`：`DEC-013` 固定 cursor 必填且非负，limit 默认 100、范围 `1..200`，`snapshotUpperBound < cursor` 为 400；cursor 或提供上界大于当前服务端最大消息 ID 为 `409 SyncCursorInvalid`，不得夹断/归零。
- `已验证`：消息不可变、SQLite AUTOINCREMENT 已通过真实 migration/不复用验证，当前仍是单服务实例和单 SQLite 主库，未改变 `DEC-003` 前提。
- `已验证`：每次请求使用 Microsoft.Data.Sqlite `deferred:true` Serializable 只读事务，让 actor/当前最大 ID 与页面处于同一数据库快照，且不以立即事务争抢写锁；消息先限 `limit+1` 再连接 mentions。
- `已验证`：Claude XHigh challenge #25 在 60 秒内因本机认证源优先级禁用 claude.ai connector 而超时，没有返回模型、workspace、费用或结论；按用户要求不重试、不阻塞 Codex，`DEC-013` 由已冻结协议、本地 provider API 和当前模型证据独立收敛。

## 范围

- Shared：新增 SyncResponse，固定 Web JSON 与消息集合/游标的日志脱敏边界。
- Server：新增 Sync 参数验证、状态/结果、endpoint 和只读固定上界查询服务。
- 使用同一数据库读取快照捕获首页上界和本页；续页仍核对当前最大 ID并每页动态重算权限。
- 补齐契约、参数、空库、空洞、固定快照、新消息隔离、多页、Private 水位、Public/Direct、撤权/加入、mentions、游标错误、禁用与查询边界测试。

## 非目标

- 不实现客户端 SQLite/逐页事务/AccountScopeId/合并器/通知 gate，不实现 SignalR、Search、附件或 UI。
- 不增加 migration、依赖、服务端游标状态、快照 token 或跨服务端写实例协议。

## 验收标准

- [ ] SyncResponse JSON、脱敏字符串、默认/边界参数和稳定错误 envelope 符合 `DEC-003`。
- [ ] 首页固定上界且多页期间新消息不进入旧快照；响应全部不变量在正常、多页、空库、空可见页和权限空洞下成立。
- [ ] Public/Direct/Private 当前权限准确；Private 水位过滤加入前/已读历史，History 不受影响；每页撤权/加入变化即时生效。
- [ ] cursor/上界超出当前最大 ID 为 409；负 cursor、越界 limit、上界小于 cursor 为 400；禁用/未认证遵循既有 401。
- [ ] mentions 不丢失且不会改变按消息计数的 limit；数据库读取边界、日志、Fast/Full、model drift、漏洞审计、白名单、空白与固定差异复核通过。

## 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变消息不可变、AUTOINCREMENT、单服务/单 SQLite 或固定上界前提。
- 需要服务端持久快照、跨实例同步世代、客户端本地状态或新增 migration/大型依赖。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `9500836` 为刚通过 Full、181 项测试、model drift 与漏洞审计的 around 集成头 |

### 下一步

- 按 `DEC-003/013` 实现 Shared 契约、deferred 只读快照、权限化页面、endpoint 与自动化验证。
