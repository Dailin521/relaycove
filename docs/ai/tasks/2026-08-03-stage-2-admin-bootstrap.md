# 阶段 2：默认管理员引导与管理员创建用户

## 任务定义

- **任务名称：** 阶段 2 — 一次性默认管理员引导与管理员创建用户
- **状态：** 已完成
- **基准提交：** `80bb74270e5b15a47fb4bbc7ae19deacd47f22ec`
- **工作分支：** `agent/stage-2-admin-bootstrap`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 7.1、8.2、17.4、18.2、阶段 2；`DEC-004`–`DEC-006`

### 目标

在不提交默认凭据、不自动迁移数据库的前提下，支持运维通过外部配置在空用户库中一次性创建首个管理员；随后仅已认证且数据库当前仍为管理员的账号可调用 `POST /api/admin/users` 创建普通用户或管理员，新账号可立即使用既有认证闭环登录。

### 已知事实

- `已验证`：基准 `80bb742` 的 Fast 通过，Debug 构建 0 警告、0 错误；Server 58、Shared 13、Client/Updater 各 1，共 73 项测试通过，工作树干净。
- `已验证`：工程方案阶段 2 只要求“启动时支持创建默认管理员”和“管理员创建用户”；禁用、删除、重置密码和管理员 UI 位于阶段 11，不应提前并入本切片。
- `已验证`：现有 Users 唯一 `NormalizedUserName`、IdentityV3 100000 iterations、固定 UTC/GUID、动态禁用检查、严格 bearer validation 和稳定 401/403 envelope 已通过真实 HTTP/SQLite 测试；应用启动明确不自动迁移。
- `已验证`：[NIST SP 800-63B-4](https://pages.nist.gov/800-63-4/sp800-63b/authenticators/)要求单因子密码至少 15 个字符、建议允许至少 64 个字符、不得附加字符类型组合规则，并要求建立常见/上下文弱密码 blocklist；密码应完整验证而不是截断。
- `已验证`：[ASP.NET Core 10 hosted service 官方文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)说明 `StartAsync` 默认在服务开始处理请求前执行且应保持短小；hosted service 为单例，使用 scoped 数据库服务时必须显式创建 scope。
- `已验证`：[ASP.NET Core 10 授权 handler DI 官方文档](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/dependencyinjection?view=aspnetcore-10.0)明确使用 EF 的 authorization handler 不得注册为 singleton；本任务应从数据库动态读取 `IsAdmin`，而不是把可变权限固化进 access token。
- `未验证`：Claude XHigh challenge #19 对固定 `ChallengeHead=0480b01` 在 120 秒上限内未返回结论；本机认证源优先级禁用了 claude.ai connector。按用户要求未重试，以下决策由 Codex 根据仓库、NIST 与 Microsoft 官方证据独立收敛。

### 假设

- `假设`：新增外部 `BootstrapAdmin` 配置，默认 `Enabled=false` 且仓库不写用户名、密码或占位机密；启用时必须同时提供合法 `UserName`、`DisplayName`、`Password`，缺失/畸形或禁用但仍提供凭据均启动失败，错误不得回显密码。
- `假设`：bootstrap 只在 Users 表为空时于短事务内创建一个 `IsAdmin=true` 用户；库内已有任意用户时不得创建、提权、改密或覆盖现有账号。启用时 schema 缺失、数据库锁定或写入失败使启动失败，不自动迁移、不静默降级；成功后运维必须移除 bootstrap 凭据并禁用开关。
- `假设`：统一 `PasswordPolicy` 以 Unicode scalar value 计数，允许 15–128 个字符和空格/Unicode，不做大小写/数字/符号组合要求；拒绝常见弱密码以及与用户名、昵称或 `RelayCove` 相同/直接派生的上下文密码。登录仍按原始完整字符串验证，不在此兼容切片改变既有哈希规范化语义。
- `假设`：新增 `CreateUserRequest(UserName, DisplayName, Password, IsAdmin)` 与不含密码/哈希的 `AdminUserResponse`；request `ToString()` 必须脱敏。创建成功返回 `201`，重复规范化用户名返回稳定 `409 UserNameAlreadyExists`，结构或密码策略失败返回 camelCase `400 ValidationFailed`。
- `假设`：新增 scoped 管理员授权 handler，每次请求按 bearer `sub` 查询用户仍存在、未禁用且 `IsAdmin=true`；未认证保持 `401 AuthenticationRequired`，普通用户保持 `403 AccessDenied`。创建事务在写锁内再次确认操作者管理员状态并插入用户，避免授权检查与提交之间未来发生角色变更时产生 TOCTOU。
- `假设`：管理员创建操作只记录 actor/user ID 与结果，不记录请求对象、用户名、昵称、密码或哈希；并发创建同一规范化用户名必须恰好一个 `201`、其余 `409`，不暴露数据库异常。

### 范围

- 必须实现：
  - Shared 创建用户契约、响应与稳定冲突错误码，所有敏感 `ToString()` 脱敏。
  - 统一密码策略及自动化边界测试；同一策略供 bootstrap 与管理员创建用户复用。
  - 默认关闭、外部凭据驱动、空库一次性的 bootstrap options/validator/hosted service，不自动迁移。
  - 动态数据库管理员 policy/handler，以及 `POST /api/admin/users` 的结构验证、事务、审计日志和稳定错误语义。
  - 真实临时 SQLite + HTTP host 测试 bootstrap 幂等/非覆盖、授权、创建后登录、重复/并发、密码策略和无机密日志。
  - 新增决策记录，冻结 bootstrap 凭据生命周期、密码策略、管理员动态授权和阶段 11 边界。
- 允许修改：
  - `src/RelayCove.Shared/**`
  - `src/RelayCove.Server/**`
  - `tests/RelayCove.Shared.Tests/**`
  - `tests/RelayCove.Server.Tests/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - 本任务文件
- 明确不做：
  - 不实现禁用、删除、恢复、改角色、重置密码、列出用户、个人资料、管理员 UI、服务器状态或附件设置；这些留在阶段 11 对应切片。
  - 不开放注册，不加入 ASP.NET Core Identity schema/cookie，不改变 JWT/refresh 协议，不引入邮件、临时密码投递、强制首次改密或外部身份提供方。
  - 不自动执行 migration，不在 appsettings、测试快照、日志、错误或 Git 中保存真实 bootstrap/admin 凭据，不生成通用默认用户名或密码。
  - 不引入大型泄漏密码数据库、联网密码检查 API、账号锁定或分布式多实例 bootstrap；v1 单 SQLite 写实例边界保持不变。

### 验收标准

- [x] bootstrap 默认关闭且无凭据时不访问 Users 表；启用但配置非法或 schema 未迁移时启动失败且不泄露密码。
- [x] 启用 bootstrap 的已迁移空库恰好创建一个可登录管理员；重启或已有任意用户时不创建、覆盖、提权或改密，数据库只存 IdentityV3 hash。
- [x] 未认证、普通用户、已禁用管理员分别得到稳定 401/403/401；只有数据库当前管理员可创建普通用户或管理员，创建后可通过既有登录/me 闭环验证角色。
- [x] 用户名、昵称和密码边界返回稳定 camelCase validation；重复用户名返回 409；同名并发创建只有一个成功且无 SQLite/唯一索引细节泄漏。
- [x] 密码策略覆盖 15/128 Unicode scalar、空格/Unicode、弱密码与上下文密码；请求、日志、错误和数据库均不含明文密码或 token/hash 泄漏。
- [x] Fast、Full、漏洞审计、文件白名单、`git diff --check`、Codex 固定差异自审与 Claude challenge/review 均通过或按规则如实记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check '80bb74270e5b15a47fb4bbc7ae19deacd47f22ec..HEAD'
```

### 停止并询问

- 证据要求改变“不开放注册”、管理员由服务端数据库动态授权、单 SQLite 写实例或应用不自动迁移等架构边界。
- 必须提交默认凭据、引入外部凭据投递/身份服务/大型泄漏密码依赖，或无法证明 bootstrap 不会覆盖/提权已有账号。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现一次性空库 bootstrap 与管理员创建用户，不提前实现阶段 11 用户维护或 UI。
bootstrap 默认关闭，所有凭据仅从外部配置注入；不迁移、不覆盖、不提权、不改密。
管理员权限每次从数据库读取，并在创建事务内再次确认；密码与请求对象不得进入日志。
Fast 后形成代码检查点，Full 后固定 ReviewHead；绿色后按用户授权仅快进集成并推送，不触碰 main/Tag/Release/部署。
```

## 任务结果

### 修改摘要

- 新增 Shared `CreateUserRequest`/`AdminUserResponse` 与 `UserNameAlreadyExists`；所有创建请求字符串表示脱敏密码，响应不含密码或哈希。
- 新增按 Unicode scalar 计数的 15–128 字符密码策略、控制字符/畸形 Unicode/常见与上下文弱密码拒绝，以及 bootstrap/API 共用的新用户输入校验。
- 新增默认关闭的 `BootstrapAdmin` options/validator/hosted service：未配置时不触库，启用时要求外部完整凭据，只在已迁移空库的 Serializable 写事务内创建首个管理员，非空库不覆盖、提权或改密。
- 新增 scoped 数据库管理员 policy/handler、事务内 actor 二次复核与 `POST /api/admin/users`；创建成功 201，结构错误 400，非管理员 403，重复规范化用户名 409，同名并发单赢。
- 测试宿主改为测试代码先显式 migration、再启动应用，既支持真实 bootstrap 启动证据，也继续证明应用自身不自动迁移；审计日志只出现 actor/created ID 和角色结果。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 Fast | Debug 0 警告、0 错误；73 项测试通过 |
| `未验证` | Claude challenge #19 | `claude_second_brain` 在 120 秒后因 connector 被本机认证源禁用而超时；无模型、workspace、费用或审查结论，ChallengeHead 与工作树保持不变 |
| `已验证` | Server 定向与全量测试 | `83/83` 通过；含 bootstrap 关闭不触库、非法配置/未迁移失败、空库一次创建、非空不变、管理员授权、创建后登录、同名并发、密码 scalar/Unicode/弱口令和日志无机密 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 构建 0 警告、0 错误；Server 83、Shared 15、Client/Updater 各 1，共 100 项测试通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | format、Release 构建、100 项测试和 `git diff --check` 全部通过；构建 0 警告、0 错误 |
| `已验证` | `dotnet list RelayCove.sln package --vulnerable --include-transitive` | 8 个源/测试项目的直接与传递依赖均未报告已知漏洞 |
| `已验证` | 基准差异文件白名单 | `80bb742..HEAD` 共 28 个文件，关闭 Git 中文路径转义后 0 个超出任务允许范围 |
| `已验证` | Codex 固定候选自审 | `DecisionHead=acaed1d..ReviewHead=419ef00`；确认动态授权、事务、取消、日志、错误和测试，补齐 handler 请求取消、畸形 Unicode 与 actor 复核审计后 Fast/Full 通过 |
| `未验证` | Claude 候选 review | 用户明确要求 Claude 仅作参考且避免长耗时；前置 #19 已因本机 connector 配置超时，故未重复候选调用，由 Codex 固定差异自审与本地验证降级覆盖 |

### 文件范围

- 新增：Shared 管理员用户契约；Server password/bootstrap/authorization/admin endpoint 服务；对应 Shared/Server 测试与本任务文件。
- 修改：Server 启动、Shared 稳定错误码、Server 测试宿主、工程方案、`DECISIONS.md`、`STATUS.md` 与 `V1_EXECUTION.md`。
- 删除：无。

### 决策与限制

- 决策：采用默认关闭、外部凭据、整表空库一次性的 bootstrap；15–128 Unicode scalar、无组合规则、带最小弱密码/上下文拒绝的共享密码策略；scoped 动态管理员 handler 与事务内 actor 复核；详见 `DEC-007`。
- 已知限制：v1 使用小型内置弱密码集合与上下文规则，不是完整泄漏密码语料库；bootstrap 非空库绝不自愈管理员缺失，成功后凭据移除依赖部署操作；禁用/删除/改角色/重置密码和首次改密仍留在阶段 11。
- 独立复核限制：Claude #19 和候选 review 均无结论，不能标记为通过；已由固定候选 Codex 自审、100 项测试、Full 和漏洞审计如实降级覆盖。

### 下一步

- 将完成提交仅快进合入 `agent/v1-integration`；下一切片进入阶段 3，先实现 Conversations/ConversationMembers 持久化与迁移边界。
