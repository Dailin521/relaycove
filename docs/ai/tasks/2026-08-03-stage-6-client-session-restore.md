# 阶段 6：持久 refresh 会话恢复与自动登录

## 状态

- `completed`
- 分支：`agent/stage-6-client-session-restore`
- 基线：`b722a6a75e5ef15b1f9e95ef02436499f3180fc0`

## 目标

把已完成的内存认证会话与 DPAPI credential store 连成一个无 UI 的认证入口：交互登录成功后保存 refresh token；启动时加载凭据并只发一次 refresh 恢复会话；后续 refresh rotation 持久化新 token；失效身份和 logout 清理持久凭据。明确处理“服务端已轮换但本地保存失败”的不确定持久化状态。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `b722a6a`；前序 Full 为 Release 0 警告、0 错误、339 项测试，model drift 与八项目漏洞审计通过；`main` 未改变。
- `已验证`：`DEC-022` 已固定客户端 refresh/logout 线性化且认证写请求不重试；`DEC-023` 已提供 CurrentUser DPAPI、原子 Save/Load/Clear 和 Corrupt/Unavailable 分类。
- `已验证`：服务端 refresh 是单次使用轮换；恢复响应丢失时重试旧 token 不安全。服务端成功返回后若新 token 未落盘，内存新会话仍有效，但旧磁盘 token 已失效，必须清理并显式标记未持久化。
- `已验证`：工程方案要求记录自动登录，并要求登出/切换账户取消旧作用域；本切片只形成认证入口，不创建账户 cache/SignalR/Sync runtime。
- `已验证`：Claude 调用已达 `30/30` 硬上限；本安全切片使用仓库事实、真实 HTTP/DPAPI/磁盘测试与 Codex 固定差异复核。

## 范围

- 增加单一认证入口，串行显式 Login 与 Restore；登录失败沿用稳定 HTTP 分类，Restore 先映射凭据 NotFound/Corrupt/Unavailable，再只发一次 `/api/auth/refresh`。
- Restore 成功响应必须通过既有响应/Bearer 校验且 user ID 与持久身份一致；401 或身份错配清空持久凭据，不建立会话。
- Login/Restore 成功后用不受已完成 HTTP 调用者取消影响的提交边界保存当前 refresh token，再返回会话；保存失败尽力清除旧正式凭据，返回的内存会话标记 `IsCredentialPersisted=false`。
- 会话附加内部 credential store；后续 refresh 成功后保存同一响应的新 refresh token，再原子发布内存 token。保存失败清旧磁盘 token，但内存会话继续使用已验证的新 token并标记未持久化。
- refresh 401/用户错配在清内存的同时尽力清持久凭据；失败不把会话恢复为已认证。
- logout 先清内存和持久凭据，再发远端 revoke。持久清理失败时，即使调用者已取消也仍用会话生命周期尝试一次远端 revoke，并返回明确 `CredentialClearFailed`；不重试。
- Dispose 表示应用/账户 runtime 停止，不清持久凭据，以支持下次自动登录。
- 可控 HTTP + 真实 DPAPI/磁盘测试覆盖登录保存、启动恢复/轮换、401/错配/网络不确定、保存/清除故障、后续 refresh、logout/取消和日志脱敏。

## 允许修改

- `src/RelayCove.Client/Auth/`
- `tests/RelayCove.Client.Tests/Auth/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不创建 AccountScopeIdentity/cache、SignalR、Sync、timer、MainWindow 或 UI；账户 runtime 是下一独立切片。
- 不实现多账户凭据、Remember-me 选项、主动到期前 refresh、密码保存、后台无限重试或损坏凭据自动删除。
- 不修改 Shared/服务端协议、数据库、migration、限流、依赖或 DPAPI 文件格式。
- 不把 credential store 或 raw refresh token 扩为公共程序集 API，不记录身份、服务器、token、用户名、显示名或异常 message。

## 验收标准

- [x] 登录成功保存 refresh token；启动恢复只 refresh 一次，校验持久 user ID 并在返回会话前保存轮换 token。
- [x] 后续 refresh rotation 同步更新 DPAPI 凭据；保存失败清旧值、标记未持久化但不部分更新 token，401/错配同时清会话与凭据。
- [x] logout 清本地内存/凭据并尝试远端撤销；清凭据失败有明确状态，调用者取消不能跳过必要 revoke。
- [x] Client 定向、真实 DPAPI/HTTP、关键竞态、Fast/Full、model drift、八项目漏洞审计、白名单、空白和固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~PersistentClientAuthentication" --no-restore
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ClientAuthentication" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要改变服务端 rotation/logout 协议、DPAPI 格式、保存密码/access token 或引入新依赖。
- 无法在服务端已轮换、本地保存失败时避免继续信任旧磁盘 token，或 logout 清理失败时无法尝试撤销。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增 Client 内部的持久认证入口：串行 Login/Restore，按凭据缺失、损坏、不可用和远端结果稳定分类；Restore 只发送一次 refresh，并校验轮换响应 user ID 与持久身份一致。
- Login/Restore 在返回会话前以不可取消的本地提交边界保存 refresh token；会话后续 rotation 先保存新 token 再发布内存状态，保存失败尽力清旧文件并显式标记 `IsCredentialPersisted=false`。
- 401、身份错配、无效或取消读取的 2xx 成功体视为可能已经发生服务端轮换，清空当前会话与持久凭据；可重试非 2xx 和传输不确定仍保留当前状态且不自动重试认证写请求。
- logout 先清内存和 DPAPI 文件；本地清除失败时忽略调用者取消并尝试一次远端 revoke，返回 `CredentialClearFailed`。Dispose 保留凭据供下次启动；持久认证入口在旧会话 Dispose 完成前拒绝新的 Login/Restore。
- 代码检查点为 `5dece6b577734649ef75f36c68ea25ec82b08703`；未修改 Shared/服务端协议、数据库、migration、DPAPI 格式、依赖或账户 runtime。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `b722a6a`；前序 Full/339 项测试、model drift 与漏洞审计通过 |
| `已验证` | Client 认证定向 | Release 77/77；覆盖真实 CurrentUser DPAPI 文件、可控 login/refresh/logout HTTP、启动恢复只请求一次及轮换 replacement |
| `已验证` | 失败与安全边界 | NotFound/Corrupt/Unavailable 不发 HTTP；401、用户错配、畸形/取消 2xx 清会话和凭据；429/503/传输不确定保留凭据且不重试 |
| `已验证` | 持久提交与 logout | 登录/恢复/后续 refresh 均验证保存顺序；保存失败继续可信内存会话但清旧文件并标未持久化；清除失败且调用者已取消仍发一次 logout |
| `已验证` | 会话所有权与日志 | 旧持久会话 Dispose 前第二次登录被拒绝，Dispose 后可继续；结果、会话、manager 日志不含服务器、用户、显示名、access/refresh token 或异常 message |
| `已验证` | 关键竞态固定提交 | `5dece6b` 上启动恢复、成功体取消、保存失败、轮换、无效 2xx、logout 清理失败、旧会话所有权 9 项 Release 连续 10 轮，90/90 通过 |
| `已验证` | Fast/Full | Debug/Release 均 0 警告、0 错误；Client 153 + Shared 33 + Server 175 + Updater 1 = 362 项测试全部通过 |
| `已验证` | 格式/空白/白名单 | `dotnet format --verify-no-changes`、`git diff --check` 通过；基线到固定代码提交的 13/13 个文件全部在任务白名单，代码提交仅含 9 个 Client Auth/测试文件 |
| `已验证` | EF model drift | `has-pending-model-changes --no-build` 返回自最新 migration 后模型无变化 |
| `已验证` | 依赖漏洞审计 | 未新增包；8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 固定候选 Codex 复核 | 发现并修正无效 2xx 仍保留可能失效 token、Dispose 可中断成功响应后的持久提交和不同账户会话交叉覆盖风险；复核取消、状态映射、凭据清理与日志后无剩余发现；Claude 已达 `30/30` 硬上限 |

### 下一步

- 快进集成本切片，随后组合 AccountScopeIdentity、本地 cache、Realtime、Sync 与账户切换/Dispose 生命周期。
