# 阶段 6：客户端 DPAPI 凭据安全存储

## 状态

- `completed`
- 分支：`agent/stage-6-client-credential-store`
- 基线：`dc31a9c76d7e26cdc02abd907ac1a76f3f985d2d`

## 目标

交付 Windows 当前用户作用域的单一客户端凭据文件：只保存恢复会话必需的 canonical server base URI、user ID 和 refresh token，以 DPAPI `CurrentUser` 加密并原子替换；读取时对文件大小、DPAPI 完整性、schema 和字段做 fail-closed 校验。此切片不自动发起 refresh 或接入账户运行时。

## 已冻结证据

- `已验证`：`agent/v1-integration` 本地/远端均为 `dc31a9c`；客户端认证会话 Full 为 Release 0 警告、0 错误、322 项测试，model drift 与八项目漏洞审计通过；`main` 未改变。
- `已验证`：工程方案明确要求 Windows 客户端使用 DPAPI 本地加密，并将登录令牌与服务器地址列为核心状态；当前仓库尚无凭据持久化。
- `已验证`：Microsoft 文档说明 `ProtectedData` 是 Windows DPAPI 包装；`CurrentUser` 只允许当前用户上下文解密，`LocalMachine` 可被本机任意账户解密。WPF WindowsDesktop 参考程序集已包含该 API，无需新增包。
- `已验证`：服务端 refresh token 30 天有效且每次使用轮换；access token 仅 15 分钟。本地恢复只需保存 refresh token，不能保存密码或把短期 access token 当长期凭据。
- `已验证`：Claude 账本已达 `30/30` 硬上限；本安全切片使用仓库证据、Microsoft 官方文档、真实 Windows DPAPI 测试和 Codex 固定差异复核。

## 范围

- 显式绝对 root 下只使用固定文件名，不把服务器、用户或 token 写入路径；所有解析路径必须保持在 root 内。
- version 1 明文 payload 只含 canonical server base URI、非空 user ID 和 refresh token；不保存密码、access token、显示名或设备名。
- 使用静态应用/schema entropy 与 `DataProtectionScope.CurrentUser` 调用真实 DPAPI；明文 UTF-8 byte buffer 在 Protect/Unprotect 后用 `CryptographicOperations.ZeroMemory` 清除。
- 保存时在同目录写临时 ciphertext，异步 flush 后用 `File.Replace` 或同卷 `File.Move` 发布；失败不破坏既有目标，临时文件尽力清理。
- 读取在分配前限制 ciphertext 大小，Unprotect 后再次限制明文大小；schema、canonical URI、user ID、refresh token 任一不合法都返回 Corrupt，不建立恢复凭据、不自动删除证据文件。
- 清除是幂等操作；I/O/权限失败必须返回失败，不能假装持久凭据已删除。
- 单实例内用异步 gate 串行 Save/Load/Clear；取消按常规传播，不记录路径、URI、user ID、token、payload 或异常 message。
- 真实 Windows 文件系统与 DPAPI 测试覆盖保存/读取、轮换覆盖、ciphertext 脱敏、篡改/截断/超限、缺失、清除、取消、路径与结果脱敏。

## 允许修改

- `src/RelayCove.Client/Auth/`
- `src/RelayCove.Client/RelayCove.Client.csproj`（仅在参考程序集不足时增加官方 ProtectedData 包）
- `tests/RelayCove.Client.Tests/Auth/`
- `docs/ai/DECISIONS.md`
- `docs/ai/STATUS.md`
- `docs/ai/V1_EXECUTION.md`
- 本任务文件

## 非目标

- 不实现自动登录、启动 refresh、认证会话恢复构造、主动到期前 refresh、账户 runtime、SignalR/Sync 接线或 UI。
- 不保存密码、access token、用户名、显示名、设备名、消息数据或多个账户历史。
- 不使用 `LocalMachine`、自定义加密算法、仓库密钥、注册表/Credential Manager、数据库或云同步。
- 不自动删除或覆盖损坏的正式凭据文件；恢复失败由后续组合层显式呈现并决定清除。

## 验收标准

- [x] 真实 DPAPI CurrentUser round-trip 后字段一致，磁盘 bytes 不含服务器、user ID 或 refresh token 明文。
- [x] 保存同目录原子发布并能覆盖轮换 token；取消/失败不宣称成功，清除失败不宣称已删除。
- [x] 缺失、篡改、截断、超限、非法 schema/字段稳定 fail-closed，结果、日志和 `ToString` 脱敏。
- [x] Client 定向、Fast/Full、model drift、八项目漏洞审计、白名单、空白和固定差异复核通过。

## 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --filter "FullyQualifiedName~ClientCredential" --no-restore
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
```

## 停止并询问

- 需要保存密码/access token、采用 LocalMachine、跨用户/跨机器恢复、自定义密钥管理或新增主要安全依赖。
- 无法证明替换失败保留既有凭据，或不能在损坏/错误用户 DPAPI 数据上 fail-closed。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增 Client 程序集内部的 version 1 credential payload/read outcome/store；固定文件名不含身份，payload 只含 canonical server URI、user ID 和 refresh token，所有自动字符串输出脱敏。
- 使用 Windows `ProtectedData` + `CurrentUser` + 固定应用/schema entropy；Protect/Unprotect 的明文与 ciphertext byte buffer 在使用后清零，未保存密码或 access token，未新增 NuGet 包。
- 保存经同目录 WriteThrough 临时文件和异步 flush 后，以 `File.Replace`/首次 `File.Move` 原子发布；单 store 异步 gate 串行 Save/Load/Clear，正式/临时清除失败均如实返回失败。
- 读取在解密前后限制大小并严格验证 schema、canonical URI、user ID 与 refresh token；错误用户/篡改/截断/非法 payload fail-closed 且保留正式文件。
- 代码检查点为 `82267b785fa6ef7d04de4906b9b01de0e0cfda54`；未修改 Shared/服务端协议、数据库、migration、依赖、自动登录或账户 runtime。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 集成基线 | `agent/v1-integration` 本地/远端均为 `dc31a9c`；前序 Full/322 项测试、model drift 与漏洞审计通过 |
| `已验证` | Client credential 定向 | Release 17/17；真实当前 Windows 用户 DPAPI round-trip，磁盘 bytes 不含 canonical URI、user ID 或 refresh token 明文 |
| `已验证` | 原子与轮换 | 首次 Move、已有文件 Replace、12 个并发 Save 串行完成；目标/临时文件锁导致失败时旧正式凭据仍可读取且未发布新 token |
| `已验证` | fail-closed | 缺失、DPAPI 篡改、截断、64 KiB 超限、非法 schema、非 canonical URI、非法 token、路径是目录、取消与幂等 Clear 全部稳定通过 |
| `已验证` | 关键文件竞态 | 轮换、临时锁、正式文件替换锁、并发 Save、取消 5 项 Release 连续 10 轮通过 |
| `已验证` | Fast/Full | Debug/Release 均 0 警告、0 错误；Client 130 + Shared 33 + Server 175 + Updater 1 = 339 项测试全部通过 |
| `已验证` | 格式/空白/白名单 | `dotnet format --verify-no-changes`、`git diff --check` 通过；候选代码只含 5 个 Client Auth 文件/修改和 1 个凭据测试文件 |
| `已验证` | EF model drift | `has-pending-model-changes --no-build` 返回自最新 migration 后模型无变化 |
| `已验证` | 依赖漏洞审计 | 未新增包；8 个源/测试项目直接与传递依赖均未报告已知漏洞 |
| `已验证` | 固定候选 Codex 复核 | 发现并修正临时 ciphertext 清理失败却返回成功、原子替换失败保留旧值的直接验证、损坏 URI 异常边界及 raw credential 公共表面；复核 DPAPI scope、明文清零、文件发布/读锁、取消和日志后无剩余发现；Claude 已达 `30/30` 硬上限 |

### 下一步

- 快进集成本切片，随后使用安全存储的 refresh token 实现显式会话恢复和自动登录，再组合账户 runtime。
