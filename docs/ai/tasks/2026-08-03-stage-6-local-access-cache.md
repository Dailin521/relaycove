# 阶段 6：账户隔离本地缓存与撤权 fail-closed

## 状态

- `in_progress`
- 分支：`agent/stage-6-local-access-cache`
- 基线：`f76b95fd80c88a4c3abe39ca84c4d1efebc44d9d`

## 目标

交付首个可持久化的客户端消息入口：每个 server/user 组合只访问自己的 AccountScopeId SQLite；权威会话已存在时 Realtime 消息按稳定唯一键原子合并，ConversationAccessRevoked 必须先进入进程 deny-set，再持久化 tombstone 并清除该会话，使随后迟到消息无法复活缓存。该切片闭合阶段 5 撤权验收，但不实现 HTTP 会话对账或 Sync 分页。

## 已冻结证据

- `已验证`：集成本地/远端均为 `f76b95f`，前序 Full 为 Release 0 警告、0 错误，共 220 项测试；`main` 本地/远端仍为 `b823308`。
- `已验证`：客户端目前只有实时连接/FIFO sink，没有本地数据库、认证会话或消息存储；`DEC-016` 保证撤权事件完成前不会调用随后到达的消息 sink。
- `已验证`：工程方案固定 AccountScopeId 为 canonical server URI + 当前 user GUID 的 SHA-256 Base64Url；LocalMessages 以可空唯一 ServerMessageId 和 `(SenderId, ClientMessageId)` 合并，未知或 revoked conversation 不得自动创建。
- `已验证`：工程方案固定撤权顺序为进程 deny-set→独立最小 tombstone 事务→清理；tombstone 首次持久化失败时整个作用域 fatal fail-closed，冷启动须先权威会话对账才可展示私有缓存。
- `已验证`：Microsoft.Data.Sqlite 官方说明 SQLite 不支持真正异步 I/O，应避免伪 async API；并发访问每次新建 connection、不要跨线程共享 ADO.NET 对象；WAL 改善并发，shared cache 与 WAL 不应混用；写事务为 Serializable，busy/locked 自动重试到 timeout。
- `已验证`：本机后台无工具 Claude #30 实际使用 `claude-opus-5` 返回 `REVISE`；采纳 durable revocation intent、冷启动默认隐藏旧缓存、读写双门禁、固定唯一键顺序和撤权不可因调用方取消而丢弃，记录为 `DEC-018`。Claude 调用达到 `30/30` 硬上限，不再追加调用。
- `已验证`：首次 Full 后漏洞审计发现 `Microsoft.Data.Sqlite 10.0.10` 的最低传递解析带入有 High advisory 的 `SQLitePCLRaw.lib.e_sqlite3 2.1.11`；官方 NuGet 显示依赖下限允许升级且 2.1.12 已发布，直接固定同 bundle 家族 2.1.12 作为最小安全覆盖，记录为 `DEC-019`，须重新执行 Full 与漏洞审计。

## 范围

- Client 增加 `Microsoft.Data.Sqlite 10.0.10` 并直接固定其原有传递 bundle `SQLitePCLRaw.bundle_e_sqlite3 2.1.12` 以避开 2.1.11 High advisory，实现 canonical server base URI 与 AccountScopeId；数据库、缓存目录只接受显式绝对 root 并位于 `<root>/<AccountScopeId>/`。
- 建立当前切片实际使用的 schema/version：LocalConversations、LocalMessages、LocalMessageMentions、RevokedConversations 和 LocalAppState；启用 foreign keys、WAL、明确 timeout 和参数化 SQL，不使用 shared cache或跨调用共享 connection。
- 增加账户作用域 cache/store 与 `IRealtimeEventSink` 实现。只有调用方以权威会话 DTO 显式登记后才接收消息；未知会话触发对账请求并拒绝入库，不自动创建。
- Realtime 合并按 ServerMessageId 与 `(SenderId, ClientMessageId)` 统一：新建、pending 提升、精确重复或不可变载荷冲突得到确定结果；事务提交后才对上层可见。
- 撤权先同步更新 deny-set，再以 LocalAppState 独立提交 durable intent，随后事务持久化 tombstone、级联删除会话消息/mentions 并清 intent；迟到消息在触库前拒绝。tombstone 写失败设置作用域 fatal fail-closed，重启先重放 intent；每个新 store 默认不授权旧会话，只有当前权威 DTO 显式登记后才可读取/合并，且 tombstone 会拒绝重新登记。
- 真实磁盘 SQLite 测试覆盖跨重启 tombstone、账户/服务器隔离、路径逃逸、并发重复/冲突、pending 提升、未知会话、撤权与消息竞争、故障注入及日志脱敏。

## 允许修改

- `src/RelayCove.Client/`
- `src/RelayCove.Shared/`
- `tests/RelayCove.Client.Tests/`
- `tests/RelayCove.Shared.Tests/`
- 对应 `.csproj`、`AssemblyInfo.cs`
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现登录/refresh/DPAPI、Complete=true HTTP 会话获取、Sync 分页/游标、History、SendResponse、read-through、通知或 WPF 展示。
- 不实现附件元数据/文件、LocalAttachments、搜索、未读派生或发送 HTTP；测试可通过专用 store API 建立权威会话和 pending 行。
- 不把 SQLite 放到 UI 线程，不引入 EF Core、ORM、通用 repository、后台服务容器或 schema 占位目录。
- 不允许普通 403、未知消息或任意实时事件自行删除/恢复会话；只有专用撤权事件和后续完整权威对账可改变 tombstone。

## 验收标准

- [ ] 相同 canonical server/user 得到同一 scope，不同 server path/port/user 严格隔离；数据库文件无法逃出显式 root，scope/log 不含 token 或用户名。
- [ ] 已登记会话的 Realtime 消息按两唯一键得到 Inserted/PendingPromoted/Duplicate/Conflict；多 mention 不改变消息唯一性，并发重复只有一行。
- [ ] 撤权首先建立 deny-set，事务后 tombstone 跨重启存在且会话/消息/mentions 清空；与迟到消息并发时提交后绝不出现复活行，未知会话也不自动创建。
- [ ] tombstone 首次持久化失败使整个 scope fatal fail-closed；失败不能移除 deny-set、恢复读取或被后续消息静默绕过。
- [ ] Client/Shared 定向测试、既有回归、Fast/Full、漏洞审计、文件白名单、空白与固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~LocalCache" --no-restore
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- AccountScopeId canonicalization 必须改变工程方案的稳定输入或兼容格式。
- 需要第三方 ORM、数据库加密库、后台服务框架或改变现有 SignalR sink 契约。
- 无法在不实现 Complete=true HTTP 对账的情况下安全区分“测试/权威登记”和实时未知会话，导致生产 API 可旁路 tombstone。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 进行中。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `f76b95f`，前序 Full/220 项测试、model drift 与漏洞审计通过 |

### 下一步

- 固定本地 schema、事务与 fatal fail-closed 决策后实现最小 store/sink 与真实磁盘竞争测试。
