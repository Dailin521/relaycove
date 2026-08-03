# 阶段 6：单账户 runtime 与切换生命周期

## 状态

- `in_progress`
- 分支：`agent/stage-6-account-runtime`
- 基线：`711de0b57acba6788cf834e052b1d31b3a695068`

## 目标

把已完成的持久认证 session、AccountScopeIdentity、账户隔离本地 cache、Realtime FIFO sink 和 Sync single-flight 组合成一个无 UI 的单账户 runtime。固定启动、自动重连补拉、显式同步触发、logout、应用停止和切换账户的所有权/Dispose 顺序，保证旧作用域完全停止后新账户才能接管单一持久凭据。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `711de0b`；前序持久会话恢复 Full 为 Release 0 警告、0 错误、362 项测试，关键 9 项 90/90、model drift 与八项目漏洞审计通过；`main` 未改变。
- `已验证`：`ClientAuthenticationSession` 同时提供动态 access token、服务器 URI/user ID 与最终 Dispose；`PersistentClientAuthentication` 在旧 session Dispose 完成前拒绝新 Login/Restore。
- `已验证`：`AccountScopeIdentity` 从 canonical server URI/user ID 派生唯一目录；cache 已实现冷启动 intent 重放、权威登记门、deny-set/tombstone 与 fatal fail-closed。
- `已验证`：`ClientRealtimeConnection` 提供 FIFO 状态/消息/撤权 sink、默认有限自动重连和可等待 Dispose；`ClientSyncCoordinator` 已提供 Startup/Reconnect/WindowActivated/Periodic single-flight、调用者取消只取消等待且 Dispose 才取消共享循环。
- `已验证`：先 Sync 后连接会在同步完成与 SignalR 建立之间留下无实时覆盖的消息窗口；先连接后 Sync 时，未知会话消息仍由 cache fail-closed 拒绝并请求权威对账。
- `已验证`：Claude 调用已达 `30/30` 硬上限；本架构/可靠性切片使用仓库证据、可控生命周期组件、真实 cache/认证组合测试和 Codex 固定差异复核。

## 范围

- 新增内部 runtime factory，只接受已认证 session、长生命 HttpClient、绝对账户数据 root 与 logger factory；从 session 身份创建唯一 AccountScope/cache/Sync/Realtime 组合。
- runtime 并发 Start 只执行一次：先尝试 Realtime Start，再执行 Startup Sync；Realtime 初始失败分类并保留显式重试能力，但不能阻止 HTTP Startup Sync。
- 组合 Realtime sink：消息/撤权仍经既有 LocalCache sink；自动 Reconnecting→Connected 和未知会话请求以非阻塞方式触发既有 Reconnect Sync single-flight。
- 暴露显式 WindowActivated/Periodic Sync 与 Realtime retry，不增加 timer 或后台无限重启。
- Dispose 依序停止 Realtime、取消/等待 Sync、等待启动收敛、关闭 cache、最后 Dispose session；保留 DPAPI 凭据。Logout 复用同一终止链并在 cache 收口后调用 session logout，再 Dispose session。
- 生命周期终止只执行一次；终止后拒绝 Start/Sync/retry/logout 新请求。调用者取消只取消对共享 Start/Logout 的等待，不跳过实际清理。
- 测试覆盖身份派生、实际 cache 创建、先连接后同步、初始连接失败仍同步、并发 Start、自动重连/未知会话非阻塞触发、终止顺序、logout、取消和 dispose-before-switch。

## 允许修改

- `src/RelayCove.Client/Accounts/`
- `src/RelayCove.Client/Realtime/ClientRealtimeConnection.cs`
- `src/RelayCove.Client/Sync/ClientSyncCoordinator.cs`
- `tests/RelayCove.Client.Tests/Accounts/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不接线 App/MainWindow、DI host、登录 UI、timer、未读/通知/Toast、托盘、单实例、附件、搜索或更新。
- 不修改 Shared/服务端协议、认证/Sync/Realtime 行为、AccountScopeId 算法、SQLite schema、DPAPI 格式、migration 或依赖。
- 不实现并存的多账户 runtime、保存多个账户凭据、后台无限重试、自动重建无效游标或跨进程账户锁。
- 不让 Realtime dispatcher 等待完整 HTTP Sync，不记录服务器、root、scope ID、user ID、token、消息正文或异常 message。

## 验收标准

- [ ] factory 从同一 session 身份创建同一 scope 的 cache/Sync/Realtime，未认证 session fail-fast 且不创建目录。
- [ ] Start single-flight 先连接再 Startup Sync；Realtime 初始失败仍运行 Sync，自动重连与未知会话只请求既有 single-flight 且不阻塞事件分发。
- [ ] Dispose/logout 顺序关闭旧生产者、循环、cache 和 session；普通 Dispose 保留凭据，logout 清凭据/撤销；旧 runtime 完成前新持久登录被拒绝，完成后可切换。
- [ ] Client 定向、真实 cache/HTTP/DPAPI、关键竞态、Fast/Full、model drift、八项目漏洞审计、白名单、空白与固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ClientAccount" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要引入 UI/Host/新依赖、改变公共协议/SQLite/DPAPI 格式、允许并存多账户 runtime 或后台无限重试。
- 无法保证旧 Realtime/Sync 在 cache/session 关闭前收敛，或无法证明新账户只在旧 session Dispose 后接管凭据。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 实现未读/通知协调、窗口前后台触发与 Windows 通知可靠性入口。
