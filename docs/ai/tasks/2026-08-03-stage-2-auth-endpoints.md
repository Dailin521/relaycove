# 阶段 2：认证端点与 Token 轮换

## 任务定义

- **任务名称：** 阶段 2 — 登录、refresh 轮换、logout 与 current-user 最小闭环
- **状态：** 已完成
- **基准提交：** `cd8f33afafe4956658792b0036cd229c29277412`
- **工作分支：** `agent/stage-2-auth-endpoints`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.1、8.2、10.2、11.1、18.4、阶段 2；`DEC-004`、`DEC-005`

### 目标

提供可由真实 HTTP 客户端验证的认证闭环：有效账号可登录并获得短期 access token 与一次性 refresh token，access token 可调用 `/api/auth/me`，refresh 只能成功轮换一次，logout 可幂等撤销 refresh token；未知用户、错误密码、禁用用户和无效机密不产生账号或 Token oracle。

### 已知事实

- `已验证`：基准 `cd8f33a` 的 Fast 通过，Debug 0 警告、0 错误；Server 30、Shared 9、Client/Updater 各 1，共 41 项测试通过，工作树干净。
- `已验证`：工程方案冻结 `POST /api/auth/login|refresh|logout`、`GET /api/auth/me`，登录成功返回 `LoginResponse`，未知用户、错误密码和禁用用户统一为 `401 AuthenticationFailed`。
- `已验证`：基准已有唯一 `NormalizedUserName`、IdentityV3 密码验证、固定 UTC/GUID、hash-only RefreshTokens 与真实 SQLite migration；应用启动不自动迁移。
- `已验证`：[ASP.NET Core 10 JWT bearer 官方文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)要求 API 完整验证签名、issuer、audience 与 expiration；[CA5404](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca5404)要求保留 issuer/audience/lifetime/expiration 验证。
- `已验证`：[RFC 8725](https://www.rfc-editor.org/rfc/rfc8725.html)要求固定允许算法、足够密钥熵、issuer/subject/audience 验证和不同 JWT 用途的显式类型；[RFC 9700](https://datatracker.ietf.org/doc/rfc9700/)要求公共客户端 refresh token 使用 sender constraint 或 rotation。
- `已验证`：[Microsoft.AspNetCore.Authentication.JwtBearer 10.0.10](https://www.nuget.org/packages/microsoft.aspnetcore.authentication.jwtbearer)面向 `net10.0`；ASP.NET Core 自带 rate limiter 支持按 endpoint/IP 分区、无队列拒绝与 `Retry-After`。
- `已验证`：Microsoft 官方不建议生产系统自行从用户名/密码签发 access token，而工程方案明确要求封闭自托管 WPF 客户端采用该流程；本任务必须把偏离标准身份提供方的风险与部署密钥边界写入决策，而不能把它描述为通用 OAuth/OIDC 实现。
- `已验证`：两次只读 `claude-opus-5` / XHigh challenge 对 `ChallengeHead=87ff08a` 返回 `REVISE`；其毫秒时钟、非 deferred 写事务、JWT 默认值、error envelope、token oracle、领域时间方法、强类型 token 与限流发现经 Codex 用仓库代码和 Microsoft.Data.Sqlite 官方事务文档独立核对成立。
- `已验证`：Microsoft.Data.Sqlite 事务默认 Serializable；只有显式 `BeginTransaction(deferred: true)` 才延迟并在读后升级写锁。当前轮换应使用默认非 deferred 显式事务，且把条件更新作为第一条语句；并发证据必须来自真实文件 SQLite。

### 假设

- `假设`：单服务 v1 使用 HS256 access JWT；Base64 signing key 至少 32 随机字节，只从 User Secrets/环境或部署配置注入，缺失/畸形时启动失败，仓库不提供默认密钥。固定 `typ=at+jwt`、issuer、audience、`sub/jti/iat/exp`，validation 只允许 HS256，clock skew 为 30 秒。
- `假设`：access 生命周期 15 分钟，refresh 生命周期 30 天；唯一 `ServerClock` 包装 `TimeProvider` 并在所有持久化值和 EF 查询参数进入 converter 前截断到毫秒 UTC。access token 不携带可变管理员权限，JWT 验证后从数据库确认用户仍存在且未禁用。
- `假设`：新增 `RefreshTokenRequest(RefreshToken)`、`LogoutRequest(RefreshToken)` 与 `CurrentUserResponse(UserId, UserName, DisplayName, IsAdmin)`；refresh 复用脱敏 `LoginResponse`，logout 对缺失、畸形、未知、过期或已撤销 token 统一返回 `204`，refresh 对这些情况统一返回 `401 AuthenticationFailed`。
- `假设`：refresh 在默认非 deferred/Serializable SQLite 写事务内，第一条语句以 `TokenHash == hash && RevokedAt == null && ExpiresAt > now` 条件 `ExecuteUpdate`；受影响行数必须为 1 才插入新 token 并提交。v1 不新增 token-family 列，已撤销 token 重放不做全账号撤销；残余 `SQLITE_BUSY/LOCKED` 返回 `503 ServiceUnavailable`，不伪装为凭据失败或泄漏 token。
- `假设`：`User` 新增只接收毫秒 UTC 的领域方法，登录成功原子更新 `LastLoginAt`、`LastOnlineAt`、`UpdatedAt`，refresh 更新 `LastOnlineAt`、`UpdatedAt`；rehash-needed 在同一成功事务更新密码哈希。原始 token 与 hash 使用不同 `readonly record struct`，原始值 `ToString()` 永远脱敏，禁止同为 `string` 的调用点互换。
- `假设`：login 使用按 remote IP 的内存 fixed-window 10 次/分钟、queue 0；refresh 为 60 次/分钟，返回 `429 RateLimitExceeded` 与 `Retry-After`。真实反向代理可信转发头与分布式限流属于部署切片，当前不信任客户端自报 IP header。
- `假设`：JWT `MapInboundClaims=false`，签发与验证显式固定 `typ=at+jwt`、HS256、issuer/audience/lifetime/signature；`OnChallenge`/`OnForbidden` 只返回稳定 error envelope，`WWW-Authenticate` 不携带过期、签名或账号状态细节。新增 `ServiceUnavailable`、`InternalServerError` 作为稳定基础设施错误码。

### 范围

- 必须实现：
  - Shared 的 refresh/logout/me 契约、脱敏 `ToString()` 与 `RateLimitExceeded` 稳定错误码。
  - 强类型 JWT/refresh token 签发服务与验证配置，启动时校验非机密参数和 signing key，不向日志、错误或 `ToString()` 暴露完整 Token。
  - `/api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`、`/api/auth/me`；统一 `ApiErrorResponse`、JWT challenge/forbidden 与 validation 错误形状。
  - 登录 dummy verify、禁用检查、rehash、用户活动时间更新；refresh 条件撤销 + 轮换事务；logout 幂等撤销；me 读取当前数据库状态。
  - 仅对认证敏感端点应用可配置的内存限流；拒绝响应不得回显用户名、密码或 Token。
  - 使用真实临时 SQLite migration 和 `WebApplicationFactory` 验证成功路径、统一失败、JWT tamper/issuer/audience/expiry、禁用后 access 拒绝、refresh 并发单赢、logout 幂等、错误 envelope、限流与无机密日志边界。
  - 新增 `DEC-006`，冻结闭源自托管认证偏差、JWT validation、生命周期、rotation、logout/me 和限流边界。
- 允许修改：
  - `src/RelayCove.Shared/**`
  - `src/RelayCove.Server/**`
  - `tests/RelayCove.Shared.Tests/**`
  - `tests/RelayCove.Server.Tests/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - `CLAUDE.md`
  - 本任务文件
- 明确不做：
  - 不实现默认管理员、管理员创建/禁用用户、注册、改密、profile、客户端 token 存储或 UI。
  - 不实现完整 OAuth/OIDC authorization server、cookie、外部身份提供方、DPoP/mTLS、RSA/JWKS、多签名 key rotation 或 refresh token family schema。
  - 不在应用启动时自动迁移，不提交开发/生产 signing key、数据库或真实账号机密。
  - 不实现反向代理可信网段配置、分布式限流、账号锁定、验证码或部署层防暴力策略。

### 验收标准

- [x] 合法登录返回脱敏 `LoginResponse`，数据库只出现 refresh hash；密码、原始 refresh 和 access token 不进入日志/错误/持久化明文。
- [x] 未知/非法用户名、错误密码和禁用用户使用相同 `401 AuthenticationFailed` 形状，缺失或无效 bearer 使用 `401 AuthenticationRequired`，无账号状态 oracle。
- [x] access JWT 只接受固定算法、类型、签名、issuer、audience 与 lifetime；有效 token 可调用 me，篡改/错误 claim/过期 token 和登录后禁用账号被拒绝。
- [x] refresh 原子轮换且并发只能一个成功；旧/未知/畸形/过期 token 统一失败，新 token 可继续轮换，数据库只存 hash。
- [x] logout 对任意 token 输入统一 `204` 且有效 token 被撤销；用户活动时间、password rehash 与成功操作在同一事务推进。
- [x] login/refresh 限流、`Retry-After` 与 `RateLimitExceeded` envelope 有自动化证据，其他端点不被同一策略误限。
- [x] Fast、Full、包漏洞审计、文件白名单、`git diff --check`、Claude challenge 与候选独立复核均通过或按规则如实记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check 'cd8f33afafe4956658792b0036cd229c29277412..HEAD'
```

### 停止并询问

- challenge 或实现证据要求改变工程方案的用户名/密码登录产品边界、单服务/单 SQLite 架构，或必须引入外部身份服务、refresh family schema 等范围外公共兼容变更。
- signing key、生产账号或数据库等真实机密进入工作树；测试无法在不提交默认密钥的情况下启动；轮换无法证明并发单赢。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现 login/refresh/logout/me 的真实 HTTP 纵向闭环，不实现管理员、客户端或消息功能。
先固定干净 ChallengeHead，用 Claude XHigh challenge 反证 JWT、rotation、错误 oracle、事务和限流假设；独立判断后再写 DEC-006 和代码。
所有机密只在进程内短暂存在，测试使用临时随机 key 与 SQLite；应用启动不得自动迁移。
Fast 后形成代码检查点，Full 后固定 ReviewHead 做候选复核；不得 push、合并 main 或部署。
```

## 任务结果

### 修改摘要

- 新增严格 `at+jwt` / HS256 access token 签发和 bearer validation，启动期强制校验外部 signing key，并在 token 验证后动态确认用户仍存在且未禁用。
- 新增 login/refresh/logout/me HTTP 闭环、统一错误 envelope、认证端点 IP fixed-window 限流、dummy password verify、password rehash 与用户活动时间推进。
- refresh 使用真实 SQLite 默认非 deferred Serializable 写事务，以条件撤销作为首条语句并只在恰好命中一行时创建新 token；raw token/hash 采用不同强类型并统一脱敏。
- 自审发现并修复两项可靠性缺口：首次数据库读取的 `SQLITE_BUSY/LOCKED` 原会落成 500，现统一为 503；测试宿主的连接字符串覆盖晚于 `Program` 读取，现显式替换 `DbContext`，每个测试类真实隔离临时数据库。
- 新增真实 HTTP、临时文件 SQLite、并发轮换、锁库、JWT 负向、限流、启动配置和日志泄漏测试；仓库不含默认 signing key，应用仍不自动迁移。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 Fast | Debug 0 警告、0 错误；41 项测试通过 |
| `已验证` | Claude challenge #16 | safe-mode 只读 CLI，实际 `claude-opus-5` / XHigh，费用 `$0.82506775`，对 `ChallengeHead=87ff08a` 返回 `REVISE`；本地输出截断中段，但 F1 毫秒时钟与并发轮换阻塞项明确可复现 |
| `已验证` | Claude 定向 challenge #17 | 同一 ChallengeHead、只读 `Read/Glob/Grep`，实际 `claude-opus-5` / XHigh，费用 `$0.57057225`，完整返回 7 项 `REVISE` 修正；Codex 独立核对后纳入 `DEC-006` |
| `已验证` | Server 定向测试 | `58/58` 通过；含真实文件 SQLite refresh 并发单赢、锁库 503、JWT 篡改/claim/过期/无签名、限流、空请求、rehash、动态禁用和日志无机密 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 构建 0 警告、0 错误；Server 58、Shared 13、Client/Updater 各 1，共 73 项测试通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | format、Release 构建、73 项测试与 `git diff --check` 全部通过；构建 0 警告、0 错误 |
| `已验证` | `dotnet list RelayCove.sln package --vulnerable --include-transitive` | 8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 基准差异文件白名单 | `cd8f33a..HEAD` 共 47 个文件，关闭 Git 中文路径转义后 0 个超出任务允许范围 |
| `已验证` | Codex 固定候选自审 | `DecisionBase=9e17813..ReviewHead=b72194a`；发现并修复 SQLite 错误映射和测试数据库隔离问题，修复后 Fast/Full 通过 |
| `未验证` | Claude 候选 review #18 | `claude_second_brain` 因本机 `ANTHROPIC_API_KEY`/其他认证源优先于 claude.ai 登录而禁用 connector，未返回模型、workspace、费用或审查结论；按用户要求不重复耗时调用，由 Codex 自审与本地验证降级覆盖 |

### 文件范围

- 新增：Server authentication、endpoint、error、options、rate-limit 与 session 服务；Shared refresh/logout/me 契约；对应 Server/Shared 自动化测试。
- 修改：Server 启动与实体/哈希服务、Shared 稳定错误码、项目依赖和认证存储相关测试；工程方案、`DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md` 与本任务记录。
- 删除：无。

### 决策与限制

- 决策：challenge 后采用 `ServerClock`、严格 typed HS256 access JWT、动态用户状态、强类型 raw/hash token、非 deferred 条件轮换事务、稳定错误 envelope 与端点级 IP 限流；详见 `DEC-006`。
- 已知限制：见“明确不做”。v1 仍接受单实例内存限流、无可信代理配置、无 refresh family/replay 全族撤销、无 signing-key rotation/JWKS；这些边界已在 `DEC-006` 明示，不阻塞当前封闭自托管切片。
- 独立复核限制：前置 Claude challenge 有效且已纳入实现；最终候选 Claude MCP 因本机认证源配置失败未取得意见，不能标记为通过，但本地固定差异自审、58 项 Server 测试、Full 与漏洞审计均通过。

### 下一步

- 将完成提交仅快进合入 `agent/v1-integration`；下一纵向切片实现默认管理员引导与管理员用户生命周期，不扩展当前 token 协议。
