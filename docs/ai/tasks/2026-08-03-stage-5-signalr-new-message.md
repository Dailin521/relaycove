# 阶段 5：SignalR 服务端 NewMessage 实时投递

## 状态

- `in_progress`
- 分支：`agent/stage-5-signalr-new-message`
- 基线：`8f7c074b6889ad35f517e13738755097f1492a89`

## 目标

形成首个服务端 SignalR 纵向闭环：正常用户通过现有 JWT 连接只读 `ChatHub`，每次连接按当前数据库权限加入会话组；文字消息只有在新建事务提交后才向当前活跃且仍有内容权限的用户推送一次强类型 `NewMessage(MessageDto)`，推送故障不改变持久化和 HTTP 结果。

## 已冻结证据

- `已验证`：基线 Fast 为 Debug 0 警告、0 错误，Server 166、Shared 29、Client 1、Updater 1，共 197 项通过；工作区干净且本地/远端集成头均为 `8f7c074`。
- `已验证`：当前 `MessageCommandService.SendAsync` 在 SQLite 写事务提交后才返回 `Created`，目标唯一约束冲突的相同载荷返回 `Replay`；endpoint 可用该状态保证只有获胜的新插入请求触发一次发布。
- `已验证`：现有 JWT `TokenValidated` 会从数据库拒绝缺失/禁用用户；`sub` 是严格标准 `D` GUID，但 SignalR 默认用户标识不能假设会自动使用该 claim，必须注册唯一的 `IUserIdProvider`。
- `已验证`：ASP.NET Core 10 SignalR 官方认证文档要求浏览器 WebSocket/SSE 将 bearer token 放入 `access_token` 查询参数时，只在 Hub 路径提取；官方安全文档确认默认 Hosting Information 日志会记录查询字符串，必须将 `Microsoft.AspNetCore.Hosting` 提升至 Warning 或提供等价脱敏。
- `已验证`：官方组文档说明组成员只属于当前连接、重连不持久，且组不是安全机制；本切片每次连接从数据库重新加组，每次消息投递另查当前活跃收件人，不把旧组状态当作授权真源。
- `已验证`：服务端项目由 ASP.NET Core shared framework 提供 SignalR server；真实客户端集成测试需要在 Server.Tests 增加同版本 `Microsoft.AspNetCore.SignalR.Client` 测试依赖，不改变产品运行时依赖。
- `已验证`：Claude XHigh challenge #26 在 60 秒内因本机认证源优先级禁用 claude.ai connector 而超时；调用前后 HEAD 均为 `cb5a4d6` 且工作区干净，未返回模型、workspace、费用或结论，按用户要求不重试、不阻塞 Codex。

## 范围

- Shared/Server：强类型 `NewMessage(MessageDto)` Hub 客户端契约、只读 `ChatHub`、稳定会话组名、`sub` 用户标识和 JWT Hub 查询令牌限域。
- Server：连接时查询当前 Public/Private/Direct 可见会话并逐连接加组；消息发布时用单个权威查询计算当前活跃收件人，向用户连接投递而非依赖组授权。
- Server：只在 `Created` 且数据库已提交后尝试一次发布；`Replay`、校验/授权/幂等冲突不发布；查询或 SignalR 发送失败只记录不含正文、昵称、token 的元数据并保持既有 HTTP 结果。
- Tests：真实 TestServer + SignalR .NET client（LongPolling）覆盖认证、用户/组路由、新消息、当前权限、撤权后旧连接、重放去重、发送故障隔离和敏感日志；定向测试 query-token 提取边界。
- Docs：新增 `DEC-014`，更新工程方案、状态账本和本任务证据。

## 允许修改

- `src/RelayCove.Server/Authentication/`
- `src/RelayCove.Server/Hubs/`
- `src/RelayCove.Server/Realtime/`
- `src/RelayCove.Server/Endpoints/MessageEndpoints.cs`
- `src/RelayCove.Server/Program.cs`
- `tests/RelayCove.Server.Tests/` 及其项目文件
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现客户端 WPF SignalR 连接、自动重连、连接状态、本地合并、同步编排或 UI。
- 不实现 `ConversationAccessRevoked`、`ConversationUpdated`、presence、force logout、server notice 或服务端主动断开既有连接。
- 不新增客户端可调用的 Hub 业务方法，不让客户端自行声明/加入任意组。
- 不引入 Redis/Azure SignalR/backplane、outbox、队列、重试或跨实例保证，不新增 migration。

## 验收标准

- [ ] `/hubs/chat` 必须认证；有效正常用户可连接，缺失/失效/禁用身份失败；WebSocket/SSE 查询 token 只在该 Hub 路径提取且不会进入默认 Information URL 日志。
- [ ] SignalR 用户 ID 取严格唯一的 `sub` GUID；每个新连接按当前数据库权限加入 Public 与其成员 Private/Direct 组，Hub 不暴露任意加组方法。
- [ ] 新插入文字消息提交后向发送者和所有当前活跃授权用户投递一次完整 `MessageDto`；非成员、禁用用户和在连接后被撤权的旧连接不接收后续消息。
- [ ] 幂等重放与并发同键只有获胜插入发布一次；失败请求不发布；SignalR/收件人查询失败不回滚消息、不把 201 改成错误。
- [ ] 日志不含 access token、消息正文或显示名；投递日志只使用消息 ID、会话 ID、收件人数和异常元数据。
- [ ] SignalR 实际连接/分组/投递测试、既有回归、Fast/Full、model drift、漏洞审计、空白和固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --filter "FullyQualifiedName~SignalR|FullyQualifiedName~Realtime" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变已冻结的“先提交、后推送”、幂等或当前权限语义。
- 需要把组提升为授权真源、提供跨实例可靠实时投递，或加入新的生产基础设施/主要依赖。
- 需要在本切片实现客户端、撤权清理或其他事件，导致范围跨越两个以上子系统。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（基线） | Debug 0 警告、0 错误；197 项测试通过 |

### 下一步

- 冻结 `DEC-014` 后实现服务端 Hub、当前收件人发布边界与真实 SignalR 集成测试。
