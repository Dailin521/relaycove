# 阶段 6：客户端 Sync HTTP 编排与 single-flight

## 状态

- `completed`
- 分支：`agent/stage-6-client-sync-orchestration`
- 基线：`104b49a6a84125b538c38b9a396f7590ab1db0f0`

## 目标

交付每账户作用域的真实 HTTP 同步编排：每轮先获取并原子应用 `Complete=true` 权威会话快照，再从最后已提交游标以固定 `SnapshotUpperBound` 逐页拉取并调用已完成的本地整页事务。网络故障有界重试，`401` 同请求仅刷新一次，`409 SyncCursorInvalid` 显式阻塞，并发触发只运行一个循环并至多追加一轮。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `104b49a`；前序 Full 为 Release 0 警告/0 错误、259 项测试，model drift 与八项目漏洞审计通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：Shared 已有脱敏 `ConversationListResponse`/`SyncResponse`，服务端路由为 `GET /api/conversations` 与 `GET /api/sync?cursor=&snapshotUpperBound=&limit=`；错误 envelope 可以用 `ApiErrorCodes.SyncCursorInvalid` 鉴别受控游标错误。
- `已验证`：AccountScopedLocalCache 已提供 Complete 快照对账、LastSyncCursor 读取和同页原子提交；Realtime 和 Sync 共用唯一合并裁决。当前 Client 尚无 HttpClient 同步层或认证会话组件。
- `已验证`：.NET 10 官方指引要求复用 HttpClient/连接池，并支持递增退避的瞬态错误处理；`Retry-After` 可表示 delta 或绝对时间。本任务不新增韧性包。
- `已验证`：Claude 调用账本已达 `30/30` 硬上限；按既有规则使用 Codex 固定差异复核，不追加耗时调用。

## 范围

- Shared 增加工程方案已定数值的 `SyncReason`：Startup=1、Reconnect=2、WindowActivated=3、Periodic=4。
- Client 增加可接入登录状态的认证会话边界：每次请求读取当前 access token，被拒 token 只触发一次 single-flight refresh；token 不进 URL、记录或结果。
- 复用由组合根管理生命周期的 HttpClient；每次尝试新建 HttpRequestMessage，以 Bearer header 请求会话全集和 Sync 页并以 Web JSON 规则反序列化。
- 每个逻辑请求最多初始 1 次 + 3 次瞬态重试；网络/HTTP timeout、408、429、500/502/503/504 使用 250ms 起始、5s 封顶的指数退避加有界抖动，合法 `Retry-After` 取更长值并总上限 30s。
- `401` 刷新成功后以新 token 立即重试原逻辑请求，第二个 `401`/刷新失败终止为认证失败。`400`、非法 JSON/响应不变量是协议错误；只有 `409 + SyncCursorInvalid` 进入持续内存 block，不清 pending、不归零游标。
- 每轮先应用权威会话快照，再读本地 cursor；首页省略 upper，续页原样传递首页 upper，本地提交失败不进行网络重试。只有 `HasMore=false` 是该轮完成。
- 一个 coordinator 代表一个账户 scope；并发触发共用同一 flight。当前轮运行时的触发合并为一次 pending rerun，优先级为 WindowActivated > 未完成 Startup 恢复 > Reconnect > Periodic；补跑期间触发并入该补跑，不形成无界链。
- 账户退出/切换通过 coordinator Dispose 取消生命周期 token 并等待正在运行的循环；单个触发调用者取消只停止其等待，不取消共享 flight。
- 真实磁盘 + 可控 HTTP handler 测试覆盖成功多页、快照先行、固定 upper、网络/429/5xx、`Retry-After`、一次 refresh、400/409/非法 JSON、single-flight/优先级/最多一次补跑、调用者取消/账户 Dispose 与日志脱敏。

## 允许修改

- `src/RelayCove.Shared/`
- `src/RelayCove.Client/Sync/`
- `tests/RelayCove.Shared.Tests/`
- `tests/RelayCove.Client.Tests/Sync/`
- `RelayCove_工程落地方案.md`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现登录 UI、凭据安全存储、主动到期前 refresh、logout HTTP、账户组合根或 MainWindow 接线；本切片提供真实 HttpClient 同步循环和最小认证会话契约。
- 不实现后台周期 timer、Windows 激活钩子、SignalR sink 组合、通知/未读派生、History、SendResponse、受控数据库重建 UI 或游标自动修复。
- 不更改服务端路由、协议、权限、数据库/migration、限流策略或 Client 依赖。
- 不在 HttpClient handler 和 coordinator 各自实现一套重试，不重试本地事务失败，不记录 URI query、token、用户 ID、会话名、消息正文、cursor 或 upper 原值。

## 验收标准

- [x] 真实 HTTP 轮次严格先 Complete 快照、后固定 upper 逐页 Sync，且每页只在本地事务成功后推进。
- [x] 同一逻辑请求的网络/timeout/408/429/可重试 5xx 重用原 cursor/upper 并有界退避；`401` 只 refresh 一次，每次尝试使用新 request 和当前 token。
- [x] `400`/非法 JSON/响应不变量稳定终止为协议错误；`409 SyncCursorInvalid` 阻塞后续触发且不修改游标或 pending。
- [x] 并发触发只有一个 flight，原因优先级确定，一次运行链至多两轮；调用者取消不杀死共享 flight，Dispose 取消账户循环。
- [x] Shared/Client 定向、真实磁盘 HTTP 场景、Fast/Full、model drift、八项目漏洞审计、白名单、空白和固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ClientSync" --no-restore
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --filter "FullyQualifiedName~SyncReason" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变已发布服务端 Sync/会话/错误协议，或新增主要韧性/认证依赖才能完成。
- 无法在不归零游标或清除 pending 的情况下对 `SyncCursorInvalid` fail-closed，或不能阻止重试将同一逻辑请求变成不同 cursor/upper。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增稳定数值 `SyncReason`、脱敏运行结果与最小 `IClientAuthenticationSession` 边界，同步代码不持久化或记录 token。
- `ClientSyncHttpTransport` 复用外部 HttpClient，为每次尝试新建 GET/Bearer 请求，实现三次瞬态重试、指数抖动、`Retry-After` 30 秒上限、一次 refresh 和精确 `SyncCursorInvalid` 分类。
- `ClientSyncCoordinator` 每轮先应用 Complete 会话快照，再以磁盘 cursor 和固定 upper 逐页提交；并发触发共享 flight，用锁内先发布 TaskCompletionSource 消除完成/赋值竞态，至多补跑一轮。
- 代码检查点为 `8f7838baa79f194702cd88d3d4f6134d5f6e9341`；未修改服务端、EF 模型、migration、Client 依赖或已冻结同步协议。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `104b49a`，前序 Full/259 项测试、model drift 与漏洞审计通过 |
| `已验证` | Client/Shared 定向 | Release Client Sync 编排 25/25，Shared `SyncReason` 1/1 |
| `已验证` | HTTP 与本地事务 | 真实磁盘缓存 + 可控 HttpMessageHandler 覆盖 Complete 快照先行、多页固定 upper、精确 tuple 重试、401 仅 refresh 一次、400/非法 JSON/响应不变量和 409 block |
| `已验证` | 瞬态故障 | 网络/timeout/408/429/500/502/503/504、`Retry-After` 和 30 秒封顶全部通过；重试耗尽不推进 cursor |
| `已验证` | 竞态与取消 | 单 flight/最多一次补跑、完成后新 flight、调用者取消、Dispose 取消和 Startup 恢复优先级每轮 5 项，Release 连续 10 轮通过 |
| `已验证` | Fast/Full | Debug/Release 均 0 警告、0 错误；Client 76 + Shared 33 + Server 175 + Updater 1 = 285 项测试全部通过 |
| `已验证` | 格式/空白/白名单 | `dotnet format --verify-no-changes`、`git diff --check` 通过；固定差异仅 9 个允许的 Client/Shared/测试文件 |
| `已验证` | EF model drift | `has-pending-model-changes --no-build` 返回自最新 migration 后模型无变化 |
| `已验证` | 依赖漏洞审计 | 8 个源/测试项目直接与传递依赖均未报告已知漏洞 |
| `已验证` | 固定候选 Codex 复核 | 发现并修正 active Task 可能在赋值前完成以及 flight 尾部丢触发竞态；复核 token/header、新 request/固定 tuple、事务提交顺序、409 block 和日志脱敏后无剩余发现；Claude 已达 `30/30` 硬上限 |

### 下一步

- 快进集成本切片，随后实现真实客户端认证会话、refresh rotation 与账户 scope 组合生命周期。
