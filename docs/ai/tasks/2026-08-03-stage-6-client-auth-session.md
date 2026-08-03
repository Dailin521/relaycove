# 阶段 6：客户端认证会话与 refresh rotation

## 状态

- `completed`
- 分支：`agent/stage-6-client-auth-session`
- 基线：`b8d5955ccacb96c4024b3fbdfcdfbe43ee88af96`

## 目标

交付无 UI 的真实客户端认证会话：通过现有 HTTP 契约登录，在内存中保存 access/refresh token，以 single-flight 完成 refresh rotation，并让 logout 与 refresh 串行化且始终先清除本地会话。该会话直接实现 Sync 已冻结的 `IClientAuthenticationSession`。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `b8d5955`；前序 Full 为 Release 0 警告、0 错误、285 项测试，model drift 与八项目漏洞审计通过；`main` 本地/远端仍为 `b823308`。
- `已验证`：服务端已有 `POST /api/auth/login`、`refresh`、`logout`；refresh 采用单次使用 token 原子轮换，旧 token 重放返回 401，logout 是幂等 204。
- `已验证`：Shared 已有脱敏的 `LoginRequest`、`LoginResponse`、`RefreshTokenRequest`、`LogoutRequest`；客户端 Sync 已依赖 `IClientAuthenticationSession` 的动态 access token 与被拒 token refresh 接口。
- `已验证`：RFC 9700 建议 refresh token rotation/replay detection；响应丢失时自动重试单次使用 refresh token 会制造不确定轮换，因此本任务不自动重试认证写请求。
- `已验证`：Claude 调用账本已达 `30/30` 硬上限；本安全切片按既有账本使用 Codex 固定差异复核，不追加耗时调用。

## 范围

- 增加严格服务端基址规范化：只接受无 user-info/query/fragment 的绝对 HTTP(S) URI，保留反向代理子路径并固定尾斜杠。
- 登录每次新建 POST/JSON 请求，不重试；稳定区分成功、400、401、429、瞬态服务不可用、协议错误与其他远端失败。
- 成功响应必须验证非空用户 ID、access/refresh token、有效过期时间和版本字段；非法成功响应不得建立会话。
- 会话只在锁内保存原始 token，对外只公开必要的非敏感账户元数据；任何 `ToString` 和日志都不得包含 token、用户名、显示名或完整服务器地址。
- 相同被拒 access token 的并发 refresh 共用一个已在锁内发布的 Task；单个调用者取消只取消等待，会话生命周期取消才取消共享 HTTP。
- refresh 与 logout 共用操作门。refresh 成功原子替换 access/refresh token；401 或用户 ID 不匹配时 fail-closed 清空会话；网络、429、5xx 和协议失败不自动重试，也不部分覆盖当前会话。
- logout 等待在途 refresh，使用最新 refresh token，并在发出 HTTP 前清空本地会话；HTTP 失败或调用者取消都不得恢复本地 token。
- Dispose 取消会话级 HTTP、等待共享操作结束并清空敏感状态。
- 可控 HttpMessageHandler 测试覆盖状态分类、响应验证、轮换、并发、取消、logout 竞争、Dispose 与日志脱敏。

## 允许修改

- `src/RelayCove.Client/Auth/`
- `src/RelayCove.Client/Sync/IClientAuthenticationSession.cs`
- `tests/RelayCove.Client.Tests/Auth/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现 DPAPI、磁盘凭据存储、自动登录、主动到期前 refresh 或账户 scope 组合根；这些是后续安全/生命周期切片。
- 不实现登录 UI、MainWindow 接线、SignalR/Sync 生命周期组合、用户切换 UI、未读或通知。
- 不修改 Shared/服务端 HTTP 协议、数据库、migration、限流或依赖。
- 不自动重试 login/refresh/logout，不记录请求 body、token、用户名、显示名或服务器 URI。

## 验收标准

- [x] 真实登录建立经过完整验证且日志脱敏的内存会话，所有失败状态稳定可测。
- [x] 并发 refresh 单飞且只轮换一次；成功原子替换 token，401/用户错配清空，响应不确定失败不重试、不部分覆盖。
- [x] logout 与 refresh 串行，使用最新 refresh token 并先本地退出；失败、取消和 Dispose 均不恢复敏感状态。
- [x] Client 定向测试、Fast/Full、model drift、八项目漏洞审计、白名单、空白和固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ClientAuthentication" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变服务端认证协议、refresh rotation 语义或新增主要认证/存储依赖才能完成。
- 无法在不重试不确定 refresh 的前提下提供 fail-closed 语义，或 logout 无法与 refresh 建立确定顺序。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增严格 URI 规范化、真实 login POST、稳定失败分类与脱敏 `ClientLoginOutcome`；成功响应需通过身份、长度、过期时间、版本与真实 Bearer header 校验才建立会话。
- `ClientAuthenticationSession` 只在锁内保存 token，直接实现 Sync 认证边界；refresh 使用锁内先发布的 TaskCompletionSource single-flight，不受单个等待者取消影响，成功原子替换两枚 token，401/用户错配 fail-closed。
- refresh/logout 共用操作门；logout 等待轮换并使用最新 refresh token，在 HTTP 前清空本地状态，失败或取消不恢复；Dispose 取消共享 HTTP、等待操作并清空。
- 代码检查点为 `821d8598c8936376ba31e586bd8cfd4d23beda40`；未修改 Shared/服务端协议、数据库、migration、依赖、DPAPI 或 UI。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `b8d5955`；前序 Full/285 项测试、model drift 与漏洞审计通过 |
| `已验证` | Client 认证定向 | Release 37/37；覆盖真实 POST/body/反向代理路径、400/401/408/429/5xx/其他状态、Retry-After、网络/JSON/响应不变量与日志脱敏 |
| `已验证` | refresh rotation | 20 个并发等待者仅一次请求；成功替换 access/refresh token，单个等待取消不取消共享请求，401/用户错配清空，网络/429/5xx/非法响应不重试且不部分覆盖 |
| `已验证` | logout/Dispose | logout 等待 refresh 后使用最新 token 且进入 handler 前已本地退出；调用者取消、传输失败和 Dispose 都不恢复 token |
| `已验证` | 关键竞态 | refresh 单飞/调用者取消、refresh→logout、取消 logout、Dispose 5 项 Release 连续 10 轮通过 |
| `已验证` | Fast/Full | Debug/Release 均 0 警告、0 错误；Client 113 + Shared 33 + Server 175 + Updater 1 = 322 项测试全部通过 |
| `已验证` | 格式/空白/白名单 | `dotnet format --verify-no-changes`、`git diff --check` 通过；候选代码只含 7 个 Client Auth 文件与 1 个认证测试文件 |
| `已验证` | EF model drift | `has-pending-model-changes --no-build` 返回自最新 migration 后模型无变化 |
| `已验证` | 依赖漏洞审计 | 8 个源/测试项目直接与传递依赖均未报告已知漏洞 |
| `已验证` | 固定候选 Codex 复核 | 发现并修正私有敏感 record 自动展开风险、成功响应长度边界与无效 Bearer token 接受；复核单飞发布/完成、logout 线性化、不确定轮换无重试及日志后无剩余发现；Claude 已达 `30/30` 硬上限 |

### 下一步

- 快进集成本切片，随后实现 DPAPI 凭据存储与安全恢复，再单独组合账户运行时。
