# 阶段 6：客户端 DPAPI 凭据安全存储

## 状态

- `in_progress`
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

- [ ] 真实 DPAPI CurrentUser round-trip 后字段一致，磁盘 bytes 不含服务器、user ID 或 refresh token 明文。
- [ ] 保存同目录原子发布并能覆盖轮换 token；取消/失败不宣称成功，清除失败不宣称已删除。
- [ ] 缺失、篡改、截断、超限、非法 schema/字段稳定 fail-closed，结果、日志和 `ToString` 脱敏。
- [ ] Client 定向、Fast/Full、model drift、八项目漏洞审计、白名单、空白和固定差异复核通过。

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

- 待完成。

### 验证证据

- 待完成。

### 下一步

- 使用安全存储的 refresh token 实现显式会话恢复和自动登录，再组合账户 runtime。
