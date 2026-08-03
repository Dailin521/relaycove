# 阶段 6：read-through 安全目标与持久上传

## 状态

- `completed`
- 分支：`agent/stage-6-read-through-upload`
- 基线：`902456efa3e108a3159a86eaaa88a96271b9cafb`

## 目标

在不改服务端契约、SQLite schema 或 WPF 的前提下，把本地 `PendingReadThroughMessageId` 通过账户 runtime 安全、幂等地上报到既有 read endpoint。任何请求目标都必须是当前会话中真实存在且已本地置读的服务器消息，同时不越过已提交全局 Sync cursor 或原始 pending 水位；Realtime 与 Sync 触发共享单一上传 flight，失败不丢 pending、不紧循环、不跨账户泄漏。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `902456e`；前序本地未读切片 Full 为 413/413，History/Realtime、权威已读边界和损坏游标三条新增回归连续 10 轮 30/30，model drift 与八项目漏洞审计通过。
- `已验证`：服务端 `POST /api/conversations/{conversationId}/read` 会验证目标消息真实属于该会话，不存在、跨会话或非正目标稳定 400；成功返回单调 `ConversationReadReceipt`。
- `已验证`：`LastSyncCursor` 是所有可见消息共享的全局 ID 水位。数值 `MIN(PendingReadThroughMessageId, LastSyncCursor)` 可能属于另一会话，不能直接作为 read endpoint 目标。例如 A 会话前台消息 63、全局 cursor 60，而 A 在 63 之前的本地消息为 50，则 60 不是 A 的合法请求目标。
- `已验证`：当前 cache 已在同一事务保存原始 pending 最大值并把前台行批量置读，但 `LastReadMessageId` 暂按数值 `MIN(message.Id, cursor)` 推进；尚无外部 uploader，因此可以在首次接入 HTTP 副作用前修正为会话内真实消息目标。
- `已验证`：现有 `ClientSyncHttpTransport`、`ClientAuthenticationSession` 与账户 runtime 已冻结 Bearer 获取、一次可信 refresh、single-flight、重试分类和生命周期所有权，可复用其边界但不得复制凭据到日志或本地新状态。

## 范围

- 前台 read-through 的连续本地边界改为：当前会话中 `IsRead=true` 且 `ServerMessageId <= MIN(raw pending target, committed LastSyncCursor)` 的最高真实 ServerMessageId；没有合法行时保持原边界，不伪造全局 cursor ID。
- Storage 提供有界的 pending 上传快照，单次磁盘读取同时取得 cursor、会话、原始 pending 与安全真实目标；撤权、未知会话、损坏值和跨账户状态 fail-closed。
- 新增平台无关 read-through HTTP transport/coordinator，向既有 endpoint 发送安全目标，验证 receipt 的会话与单调边界；成功 receipt 与完整权威会话快照都是服务端权威确认，任一路径覆盖当前 raw pending 时均可在同一事务清除它，但较小 receipt/快照不得清除并发出现的更高 pending。
- Realtime 前台提交和 Sync 页/轮次推进只触发同一个账户级 single-flight。并发触发合并为至多一次补跑；每次 flight 每会话至多一个当前安全目标，瞬时失败保留 pending 并退出，不能紧循环。
- runtime 终止先停止生产者，再等待/终止上传 flight，最后释放 cache/auth；调用方取消只能取消等待，不能制造“HTTP 已接受但本地状态被任意回退”的窗口。

## 允许修改

- `src/RelayCove.Client/Storage/`
- `src/RelayCove.Client/Sync/`
- `src/RelayCove.Client/Accounts/`
- `tests/RelayCove.Client.Tests/Storage/`
- `tests/RelayCove.Client.Tests/Sync/`
- `tests/RelayCove.Client.Tests/Accounts/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不修改服务端 read endpoint、Shared request/receipt、认证协议、SignalR 协议、SQLite schema/migration、AccountScopeId、DPAPI 或依赖。
- 不实现 NotificationCoordinator、Round/Recovery gate、旧缓存通知收养、Toast、WPF 窗口事件、History/SendResponse HTTP 编排、主动打开会话 API 或对方已读回执。
- 不宣称跨进程 exactly-once；HTTP 与 receipt 使用服务端 `MAX(old,target)` 幂等语义，重启后尚未被 receipt 或权威快照覆盖的 pending 可安全重发。
- 不用全局 cursor 数值伪造某会话消息 ID，不发送原始 pending 超前目标，也不因永久/瞬时失败清除未确认 pending。

## 验收标准

- [x] 每个 HTTP 目标都能在同一账户/会话本地已读行中找到，且不超过原始 pending 与已提交 cursor；跨会话全局 cursor 反例得到真实 SQLite 回归。
- [x] Realtime、Sync 和重启遗留 pending 共用 single-flight，重复/并发触发不并行发送同一账户请求，cursor 推进后可继续上传更高安全目标。
- [x] 2xx receipt、401 refresh、429/5xx/网络失败、400/协议错、撤权、损坏本地值、取消与 Dispose/Logout 得到稳定且脱敏的结果；失败不清 pending、不紧循环。
- [x] receipt 与权威快照只单调推进；任一权威确认覆盖当前 raw pending 时才清除它，较小 receipt 或较小快照不能提前清除并发新目标。
- [x] 真实 SQLite + 可控 HTTP、runtime 组合、关键竞态、Fast/Full、model drift、八项目漏洞审计、白名单、空白与独立复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ReadThrough|FullyQualifiedName~ClientSync|FullyQualifiedName~ClientAccount" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要修改服务端/Shared 契约、增加 SQLite schema/migration、引入新依赖，或无法用会话内真实消息目标同时满足 cursor 安全与服务端目标归属。
- 需要把 Windows/WPF 通知副作用、跨进程协调或新的产品级失败策略纳入本切片。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- `AccountScopedLocalCache` 从同一 deferred 事务中的 raw pending、已提交全局 cursor、同会话真实已读行和未读空洞派生安全目标；批次按 raw 会话行稳定分页，撤权三重门禁，busy 有界重试，损坏 pending 只隔离单会话。
- 新增 read-through HTTP transport/coordinator：精确 Bearer POST、一次 401 refresh、稳定 403 撤权识别、结果/日志脱敏、账户 single-flight 与至多一次补跑；成功/永久/瞬时结果绑定权威快照 revision，避免重复和每页重试风暴。
- receipt 应用在单个 SQLite 事务中推进已知行、未读计数和连续已读边界，只在确认覆盖当前 pending 时清除；权威快照保留同等清理能力。Sync 页与前台 Realtime 提交后均触发上传，runtime 终止等待共享 flight。
- 最终代码检查点为 `8384e6166d69467377e36efa549309f891822076`；从基线到代码检查点的 24 个路径全部属于任务白名单，未增加协议、schema、migration、依赖或 Windows 副作用。

### 验证证据

- `已验证`：`pwsh ./scripts/verify.ps1 -Mode Fast` 与 `-Mode Full` 通过；Full 为 447/447（Client 237、Server 175、Shared 34、Updater 1），Release 构建 0 警告、0 错误，format 与 `git diff --check` 通过。
- `已验证`：`ClientReadThroughCoordinatorTests` 31 项在 Release 连续 10 轮 310/310；覆盖跨会话 cursor 真实目标、未读空洞、102 会话两批分页、401、网络/状态分类、快照级抑制、稳定撤权、receipt 错配/倒退、撤权与 pending 在途竞争、损坏行隔离、busy、single-flight/补跑、重启、调用方取消、Dispose 和脱敏。
- `已验证`：既有定向和 runtime 接线回归、真实 SQLite 事务/账户隔离、模型漂移检查通过；八个源/测试项目均未报告已知直接或传递依赖漏洞。
- `已验证`：Claude #36 challenge 的永久失败重复发送与 busy fatal 发现已在首个代码提交修正；#37 对 `b70e645` 的三个 P2——决策漂移、撤权批次竞争、损坏 pending 令全 scope fatal——经 Codex 复算成立并在 `8384e61` 修正。最终验收由 Codex 固定差异与本机自动化完成，实际 Claude 模型偏差如实记录，未冒充目标模型通过。
- `已验证`：本切片不读取或使用 VPS 配置，不执行真实发布；Notification/WPF、旧缓存收养和 Recovery gate 仍保持非目标。

### 下一步

- 实现旧缓存通知收养、Round/Recovery gate 与平台无关 NotificationCoordinator。
