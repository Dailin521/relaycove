# 阶段 4：固定上界 Sync API

## 状态

- `completed`
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

- [x] SyncResponse JSON、脱敏字符串、默认/边界参数和稳定错误 envelope 符合 `DEC-003`。
- [x] 首页固定上界且多页期间新消息不进入旧快照；响应全部不变量在正常、多页、空库、空可见页和权限空洞下成立。
- [x] Public/Direct/Private 当前权限准确；Private 水位过滤加入前/已读历史，History 不受影响；每页撤权/加入变化即时生效。
- [x] cursor/上界超出当前最大 ID 为 409；负 cursor、越界 limit、上界小于 cursor 为 400；禁用/未认证遵循既有 401。
- [x] mentions 不丢失且不会改变按消息计数的 limit；数据库读取边界、日志、Fast/Full、model drift、漏洞审计、白名单、空白与固定差异复核通过。

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

- 新增 Shared `SyncResponse`，固定消息列表、游标和快照上界的 JSON 形状，并让诊断字符串脱敏消息与游标。
- 新增 Sync 参数验证、稳定状态映射与 `GET /api/sync`；缺失/越界参数返回 400，伪造未来游标或上界返回 `409 SyncCursorInvalid`，认证失效返回 401。
- 新增显式 deferred Serializable SQLite 只读事务：同一快照读取正常 actor、全局消息最大 ID 与权限化页面；先取 `limit+1` 条消息再连接 mentions。
- 固定 Public、Direct、Private 的当前权限与 Private 水位语义；末页和无可见消息页直接推进到快照上界，保证跨过删除/无权限空洞且不会循环。
- 增加契约、参数、HTTP/真实 SQLite 多页、固定上界、权限变化、加入水位、撤权/重加、mentions、查询边界和日志测试。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `9500836` 为刚通过 Full、181 项测试、model drift 与漏洞审计的 around 集成头 |
| `已验证` | Server 定向测试 | `SyncEndpointTests` 与 `SyncRequestValidatorTests` 共 14 项通过 |
| `已验证` | Shared 定向测试 | `SyncContractTests` 2 项通过，JSON 与 `ToString()` 脱敏边界符合契约 |
| `已验证` | 固定上界与空洞 | 多页期间插入的新消息不进入旧轮次；末尾 Private 无权限空洞和全不可见/软删除页面均推进到全局上界且不循环 |
| `已验证` | 权限与水位 | Public 不受个人水位过滤，Direct 保持增量可见，Private 按当前成员与水位过滤；翻页间 read-through、撤权、重加及加入后新消息均按当前状态生效，History 仍返回 Private 历史 |
| `已验证` | mentions 与查询边界 | mentions 未改变按消息计数的页容量；一次服务调用保持 actor/MAX 与页面两条 `SELECT`，日志不含正文或显示名 |
| `已修正` | 测试隔离 | 将 xUnit `InlineData` 显式改为 `long`，并让共享数据库场景以各自基线游标开始；失败来自测试假设，没有服务实现失败 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 构建 0 警告、0 错误；Server 166、Shared 29、Client 1、Updater 1，共 197 项通过 |
| `已修正` | 首次 `pwsh ./scripts/verify.ps1 -Mode Full` | 仅命中 Windows CRLF/LF formatter 机械差异；运行 formatter 后 `git diff --numstat` 为空，无语义改动 |
| `已验证` | 最终 `pwsh ./scripts/verify.ps1 -Mode Full` | format 干净；Release 构建 0 警告、0 错误；197 项测试与空白检查通过 |
| `已验证` | EF model drift | `dotnet ef migrations has-pending-model-changes ... --no-build` 返回模型无变化 |
| `已验证` | 依赖漏洞审计 | `dotnet list RelayCove.sln package --vulnerable --include-transitive` 检查 8 个项目，未发现已知漏洞包 |
| `未验证` | Claude XHigh challenge #25 | 60 秒内因本机认证源优先级超时，无审查结论；按用户要求未重试且不阻塞本地验证 |
| `已验证` | Codex 固定差异复核 | `ReviewBase=0c7c767a54b3be54319e60c1e05a389c0e980737`，`ReviewHead=9d87ac2ee94cc83005e15ca3b095dcc5ea8530dd`；15 个预期文件，`git diff --check` 通过，无阻塞发现 |

### 文件范围

- Shared：`SyncResponse` 与契约测试。
- Server：Sync endpoint、验证器、操作结果/状态、查询服务、注册与 HTTP/SQLite 测试。
- 文档：工程方案、`DEC-013`、状态账本与本任务证据。

### 决策与限制

- 延续 `DEC-003` 的固定上界协议，并用 `DEC-013` 固定服务端参数、deferred 只读事务和三类会话权限过滤。
- 本切片未实现客户端 SQLite 合并/游标事务、AccountScopeId、通知 gate、SignalR、Search、附件或 UI，也未增加 migration、依赖或跨实例快照协议。

### 下一步

- 仅快进合入 `agent/v1-integration`，随后开始阶段 5 的 SignalR 服务端实时投递纵向切片。
