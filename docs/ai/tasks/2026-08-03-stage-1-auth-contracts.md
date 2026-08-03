# 阶段 1：认证共享契约

## 任务定义

- **任务名称：** 阶段 1 — 冻结登录 DTO 与稳定错误 envelope
- **状态：** 已完成
- **基准提交：** `c1f7020f2eb23867ec089d3328ab3cd6645fd5df`
- **工作分支：** `agent/stage-1-auth-contracts`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.1、8.2、10.2 节与阶段 1/2；`DEC-003`、`DEC-004`

### 目标

实现首个可由 Client 和 Server 共同引用的认证协议簇：登录请求、登录响应、稳定 API 错误响应与错误码。通过 JSON 形状、错误码稳定性和敏感字段日志脱敏测试，使后续认证实现不需要重新猜测公共契约。

### 已知事实

- `已验证`：工程方案已固定 `LoginRequest` 和 `LoginResponse` 字段，但尚未有代码或统一错误 envelope 形状。
- `已验证`：规范要求日志不得记录明文密码、完整 Token 或密钥；C# record 默认 `ToString()` 会打印构造参数，若不覆盖会泄露登录机密。
- `已验证`：同步契约已要求 `SyncCursorInvalid`、`IdempotencyKeyReuse`、`ConversationAccessRevoked` 稳定错误码。
- `已验证`：Shared 无外部包，Client 和 Server 已引用 Shared，Fast/Full 在基准上通过。

### 假设

- `假设`：ASP.NET Core 与客户端 HTTP JSON 使用 `JsonSerializerDefaults.Web` 的 camelCase 形状；协议类型本身不依赖 ASP.NET Core。
- `假设`：用户名不存在、密码错误和账号禁用对调用方统一返回 `AuthenticationFailed`，具体原因只进入安全服务端诊断，避免账号枚举。

### 范围

- 必须实现：
  - `LoginRequest`：`UserName`、`Password`、`DeviceName`、`ClientVersion`。
  - `LoginResponse`：`UserId`、`DisplayName`、`AccessToken`、`RefreshToken`、`ExpiresAt`、`ServerVersion`、`MinimumSupportedClientVersion`。
  - `ApiErrorResponse`：稳定 `Code`、非分支依据的 `Message`、可选 `TraceId` 与字段 `Details`。
  - `ApiErrorCodes` 字符串常量：`ValidationFailed`、`AuthenticationFailed`、`AuthenticationRequired`、`AccessDenied` 以及 `DEC-003` 的三个稳定码。
  - 覆盖 Login request/response 的 `ToString()`，密码、access token、refresh token 必须显示为 `[REDACTED]`，其他诊断字段可见。
  - 用 `JsonSerializerDefaults.Web` 验证精确 camelCase 属性、往返和错误 details；验证错误码唯一且认证失败不暴露账号状态。
  - 在工程方案加入错误 envelope 与安全语义，并新增已接受 `DEC-004`。
  - 更新 STATUS、V1_EXECUTION、CLAUDE 指引和任务证据。
- 允许修改：
  - `src/RelayCove.Shared/Auth/**`
  - `src/RelayCove.Shared/Errors/**`
  - `tests/RelayCove.Shared.Tests/Auth/**`
  - `tests/RelayCove.Shared.Tests/Errors/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - `CLAUDE.md`
  - 本任务文件
- 明确不做：
  - 不实现登录 Controller、密码哈希、JWT、refresh token、用户实体或数据库。
  - 不定义 refresh/logout/me 的 DTO，不建立 DTO 大全集。
  - 不加入数据注解、FluentValidation、JSON source generator 或新包。
  - 不记录、生成或使用真实密码、Token、密钥或用户数据。

### 验收标准

- [x] 四个公共类型各自独立文件，字段与工程方案一致，Shared 仍无 PackageReference。
- [x] Web JSON 属性名和 round-trip 测试通过；`ExpiresAt` 保持带 offset 的 `DateTimeOffset`。
- [x] `LoginRequest.ToString()` 不包含输入密码，`LoginResponse.ToString()` 不包含任一 Token，且明确含 `[REDACTED]`。
- [x] 7 个错误码值唯一，三个 `DEC-003` 码拼写与规范一致。
- [x] `ApiErrorResponse` JSON 可表达字段级多个错误，客户端不得按 `Message` 分支。
- [x] `DEC-004` 记录字符串码、认证枚举防护和敏感 record 日志边界。
- [x] Claude challenge 与候选 review 若不可用，完整记录 HEAD/status/返回元数据缺失并降级 Codex 复核。
- [x] Fast、Full、文件白名单、无漏洞新增与 `git diff --check` 通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
rg -n 'AuthenticationFailed|SyncCursorInvalid|IdempotencyKeyReuse|ConversationAccessRevoked|REDACTED' src tests RelayCove_工程落地方案.md docs/ai/DECISIONS.md
if (rg -n '<PackageReference' src/RelayCove.Shared) { throw 'Shared 引入了范围外依赖' }
git diff --check 'c1f7020f2eb23867ec089d3328ab3cd6645fd5df..HEAD'
```

### 停止并询问

- 必须改变 Login DTO 字段、认证枚举防护或日志不得泄露机密的安全边界。
- 必须引入新的验证/序列化包，或在本任务实现服务端认证。
- Claude 或本地证据发现错误 envelope 无法同时满足客户端分支稳定性与安全要求。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现认证共享契约，不实现认证服务。
先创建任务元数据提交并调用 Claude challenge；Claude 只读，失败则记录并降级。
用测试锁定 JSON 与 ToString 脱敏，Fast 后创建代码检查点，Full 后做候选复核。
公共协议变化同步工程方案与 DEC-004，完成后仅快进本地集成头；不得推送或合并。
```

## 任务结果

### 修改摘要

- 新增 BCL-only 登录请求/响应与统一 API 错误契约，覆盖密码和 Token 的 `ToString()` 脱敏。
- 以显式字符串字面量冻结 7 个公共错误码，并在 `DEC-004` 与工程方案中固定客户端分支、认证枚举防护和日志边界。
- 用 Web JSON 精确属性名、offset 往返、错误 details、错误码全集、账号状态禁词和构造参数漂移测试锁定公共协议。
- 第一轮 Claude 候选审查指出 `nameof` 绑定和测试自指缺口；修正后定向复审 `PASS`。复审补充的非阻塞 P3 也已用字段名/值配对断言关闭并通过最终 Full。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | Claude challenge | 调用因本机认证源优先禁用组织连接器而失败；调用前后 `ChallengeHead=6d60f9ae22a392adb75970763f260b10e53ebdbc` 与干净状态一致，未返回模型、workspace、mismatch 或费用，已降级 Codex 反证 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | 修正后 Debug 构建 0 警告、0 错误；Shared 9 项测试通过，Client/Server/Updater 各 1 项通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | 修正后 format、Release 构建、全部 12 项测试与 `git diff --check` 通过；0 警告、0 错误 |
| `已验证` | 敏感字段与 JSON 测试 | 精确 request/response/error 属性名、`ExpiresAt=2026-08-03T12:30:00+08:00`、密码/Token 脱敏、构造参数漂移和账号状态禁词均通过 |
| `已验证` | 文件白名单、Shared 依赖、漏洞审计 | Base 起 12 个文件全部在任务范围；Shared 无 `PackageReference`；8 个项目均无已知易受攻击包 |
| `已验证` | Claude CLI 候选复核（MCP 回退） | MCP 仍被旧认证环境阻断；等价只读 CLI 限定 `Read/Glob/Grep`，`ReviewHead=9a867323095c9753c96cf55985396229d9088059`、workspace=`E:\WorkSpace\RelayCove`、实际模型 `claude-opus-5`、mismatch=`false`、费用 `$0.666895`，结论 `FIX_REQUIRED`；P1/P2/P3 已在本候选修正 |
| `已验证` | Claude 定向复审 | 只读 CLI 锁定 `ReviewHead=836d1e223d2cd9026fdf935be9cb16affbf45cf8`；实际模型 `claude-opus-5`、mismatch=`false`、费用 `$0.14818675`，结论 `PASS`，五项原发现全部关闭 |
| `已验证` | 复审新增 P3 与最终 Full | 在 `35125ef4b1cba453416b915b6fb82fed0b12eb44` 增加字段名/字面量配对断言；目标 3 项测试及最终 Full 均通过，Release 0 警告、0 错误，12 项测试通过 |

### 文件范围

- 新增：`src/RelayCove.Shared/Auth/LoginRequest.cs`、`LoginResponse.cs`，`src/RelayCove.Shared/Errors/ApiErrorCodes.cs`、`ApiErrorResponse.cs`，两个对应测试文件及本任务文件。
- 修改：`RelayCove_工程落地方案.md`、`docs/ai/DECISIONS.md`、`docs/ai/STATUS.md`、`docs/ai/V1_EXECUTION.md`、`CLAUDE.md`。
- 删除：无。

### 决策与限制

- 决策：认证共享契约保持 BCL-only；稳定错误码是字符串，不使用数值 enum。
- 已知限制：本任务不证明任何认证运行时安全性；MCP 子进程仍受旧认证环境影响，本任务的成功 Claude 证据来自等价只读 CLI 回退并已如实记账。

### 下一步

- 创建服务端认证存储与密码哈希任务，并先做数据库/认证 challenge。
