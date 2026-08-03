# 阶段 5：客户端 SignalR 接收与连接状态

## 状态

- `completed`
- 分支：`agent/stage-5-client-realtime`
- 基线：`8c811cfe69e09f919d27114b543d23c683b846b9`

## 目标

在尚无登录 UI 和本地数据库的最小 WPF 骨架中，交付一个可独立验证的客户端实时连接组件：以最新 access token 连接 `/hubs/chat`，接收完整 `NewMessage` 与 `ConversationAccessRevoked`，按接收顺序串行交给单一 sink，并提供确定的连接状态。该组件不自行持久化或展示数据，为下一切片的统一本地合并与撤权 deny-set 提供唯一实时入口。

## 已冻结证据

- `已验证`：集成本地/远端均为 `8c811cf`，Full 为 Release 0 警告、0 错误，206 项测试通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：客户端目前仅有最小 `App/MainWindow`，无认证会话、HTTP client、本地缓存、DI 或连接组件；Client.Tests 仅有 1 项程序集测试。
- `已验证`：工程方案已把 `Microsoft.AspNetCore.SignalR.Client`、`ConnectionState`、`NewMessage`、`ConversationAccessRevoked` 和阶段 5 客户端连接状态列为既定设计；Server.Tests 已使用同版本 `10.0.10` 客户端包。
- `已验证`：Microsoft ASP.NET Core 10 文档说明 SignalR client/server 版本应匹配；`AccessTokenProvider` 会为每个 HTTP 请求获取 token；自动重连默认不会开启，`WithAutomaticReconnect()` 默认在 0/2/10/30 秒尝试，且不重试初始 `StartAsync` 失败；Reconnecting/Reconnected/Closed 分别表示进入重连、恢复和最终关闭。
- `已验证`：服务端 Hub 固定 `/hubs/chat`、JWT `sub` user ID、token 到期关闭连接；服务端已在新连接时重新查询权限和分组，并推送强类型方法名 `NewMessage` / `ConversationAccessRevoked`。
- `未验证`：Claude XHigh challenge #29 在 60 秒内仍因 `claude_second_brain` MCP wrapper 的认证源优先级禁用 connector 而超时；没有返回模型、workspace、费用、结论或发现，不作为本切片证据且不重试。

## 范围

- Shared 增加工程方案既定的 `ConnectionState`，数值固定为 Disconnected=0、Connecting=1、Connected=2、Reconnecting=3、ServerUnavailable=4。
- Client 增加单一实时 sink 契约与连接组件：校验/组合 server base URI，使用每请求动态 access-token provider，注册两个服务端事件，按接收顺序串行派发并隔离 sink 异常。
- 生命周期固定为：显式 Start 时 Connecting→Connected；初始连接失败为 ServerUnavailable 并把异常返回调用者；已连接断线使用 SignalR 默认自动重连并映射 Reconnecting→Connected，默认次数耗尽或非主动 Closed 为 ServerUnavailable；显式 Stop/Dispose 为 Disconnected。
- sink 的状态回调与数据事件均不得要求 WPF UI 线程；未来 UI 适配器自行 Dispatcher marshal。事件日志只允许状态、message/conversation ID 与异常元数据，不记录 token、正文、显示名或用户名。
- 使用真实内存 ASP.NET Core TestServer/SignalR 连接验证认证、token provider、完整 DTO、撤权 ID、串行顺序、状态和失败边界；保留既有 Server 端真实连接测试。

## 允许修改

- `src/RelayCove.Shared/`
- `src/RelayCove.Client/`
- `tests/RelayCove.Shared.Tests/`
- `tests/RelayCove.Client.Tests/`
- 对应 `.csproj`、`AssemblyInfo.cs`
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现登录/refresh/logout、凭据持久化、DPAPI、HTTP API client、DI host 或把连接实例接到当前空白 MainWindow。
- 不实现 LocalConversations/LocalMessages/SQLite、统一消息合并、deny-set/tombstone、撤权清理、未读、通知或 Sync；sink 只定义下一层入口。
- 不改变服务端 Hub、事件契约、JWT、分组或收件人查询，不增加客户端可调用 Hub 方法。
- 不自定义无限重连策略，不在默认自动重连耗尽后隐藏重启；后续账户/同步 orchestrator 可显式再次 Start。

## 验收标准

- [x] URI/token 边界正确：只接受无 user-info/query/fragment 的绝对 HTTP(S) base URI，连接固定相对 `hubs/chat`；token provider 在连接请求时动态读取且 token 不进入日志/状态对象。
- [x] 真实认证连接能把完整 `MessageDto` 与正确撤权 conversation ID 交给 sink；撤权事件先于随后到达的同会话消息完成处理，sink 异常不会终止事件消费或泄漏敏感载荷。
- [x] Start/Stop、初始 401/不可用、Reconnecting/Reconnected/Closed 映射为冻结状态；重复并发生命周期调用不会创建多连接或死锁，Dispose 后不可重启。
- [x] Client/Shared 定向测试、既有回归、Fast/Full、漏洞审计、文件白名单、空白与固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --no-restore
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 真实接收必须先实现登录 UI、token refresh 或本地数据库才能形成可验证行为。
- 必须改变服务端事件形状、默认重连语义，或需要新的大型运行时框架/容器才能实现。
- 必须把 sink 放到 UI 线程或在连接层直接修改缓存，导致本切片跨越既定模块边界。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- Shared 增加稳定 `ConnectionState`；Client 引入同版本 SignalR client 并新增生产 `ClientRealtimeConnection` 与单一 `IRealtimeEventSink`。
- 连接校验并保留反向代理子路径，动态读取 access token，映射初始/重连/关闭/主动停止状态；显式生命周期调用以 gate 幂等串行，sink 内 Stop/Dispose 延迟到 Hub 回调外执行以避免互等。
- NewMessage、ConversationAccessRevoked 和状态变化进入同一 FIFO 单消费者；撤权处理阻塞后续消息，单次 sink 故障仅记录脱敏元数据并继续消费。
- Client.Tests 使用真实 ASP.NET Core TestServer、认证和 LongPolling，覆盖完整 DTO、动态 token、401 后重启、默认自动重连、URI 子路径、顺序、日志和生命周期竞争。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `8c811cf`，前序 Full/206 项测试、model drift 与漏洞审计通过 |
| `已验证` | Client/Shared 定向测试 | Client 14/14、Shared 30/30 通过；真实认证 Hub 覆盖动态 token、完整 DTO、撤权顺序、初始失败重启、Reconnecting→Connected、sink 故障与回调内 Stop/Dispose |
| `已验证` | 关键竞争测试 Release 重复 5 轮 | 每轮 4/4，共 20 次通过；覆盖撤权屏障、真实 transport drop 重连和 sink 内 Stop/Dispose，无偶发死锁或乱序 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 0 警告、0 错误；Server 175、Client 14、Shared 30、Updater 1，共 220 项通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | format、Release 0 警告/0 错误、220 项测试与 `git diff --check` 通过 |
| `已验证` | EF model drift | 服务端模型自最新 migration 后无变化 |
| `已验证` | 依赖漏洞审计 | 8 个源/测试项目均未发现已知易受攻击的直接或传递包 |
| `已验证` | Codex 固定差异复核 | `ReviewBase=d42133864228c7a93f4bd5cdf2d8b6ba8573a7cb`、`ReviewHead=c3717c9455a98cbf9014e8cbd37ef2f635261cc3`；8 个实现/测试文件，完整任务 13 个白名单文件，URI/token、FIFO、状态、异常脱敏、并发和 dispose/reentry 无剩余发现 |

### 已知限制

- 当前登录/refresh/UI 尚未实现，因此组件未实例化到空白 MainWindow；真实内存 Hub 已验证产品组件，后续认证会话只需提供动态 token delegate。
- 尚无本地 SQLite、统一消息合并或撤权 deny-set/tombstone；本切片只保证唯一串行入口，下一切片必须从 sink 先落 fail-closed 状态再接缓存。
- 使用 SignalR 默认有限自动重连；初始失败或默认次数耗尽会明确显示 ServerUnavailable，由后续账户/同步 orchestrator 决定何时再次 Start。

### 下一步

- 进入阶段 6，建立 AccountScopeId 隔离的本地 SQLite/撤权 tombstone 与统一消息合并入口，使实时撤权真正拒绝迟到消息复活缓存。
