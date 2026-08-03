# 阶段 2：认证存储与密码哈希

## 任务定义

- **任务名称：** 阶段 2 — 建立用户与 refresh token 持久化安全基线
- **状态：** 进行中（候选验证完成，等待独立复核）
- **基准提交：** `0e5eefb0c44cdb024e4e455ff91b3eb542adfa8e`
- **工作分支：** `agent/stage-2-auth-storage`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 3.2、5.4、7.1、8.1、11.1、11.2、18.4、19.4、阶段 2；`DEC-001`、`DEC-004`、`DEC-005`

### 目标

建立后续登录端点可直接使用的服务端持久化与密码验证基线：仅包含 Users、RefreshTokens、可运行的首个 EF Core SQLite 迁移和基于 ASP.NET Core `PasswordHasher` 的密码服务。通过真实 SQLite 迁移与约束测试证明 schema、规范化、机密存储和验证行为，不在本任务签发 Token 或暴露 HTTP 接口。

### 已知事实

- `已验证`：工程方案已固定单 ASP.NET Core 服务、单 SQLite 主库、EF Core、Users/RefreshTokens 字段和 `/opt/relaycove/data/relaycove.db` 生产连接串。
- `已验证`：当前 Server 只有 Web 骨架且无外部包；基准 Fast 通过，Debug 0 警告、0 错误，12 项测试通过。
- `已验证`：本机为 .NET SDK `10.0.101` / runtime `10.0.1`；[NuGet 官方页](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite)在 2026-08-03 显示 EF Core SQLite/Design 最新稳定 `10.0.10`，均以 `net10.0` 为目标。
- `已验证`：[Microsoft 密码哈希文档](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/password-hashing)要求新密码登录应用使用 `PasswordHasher`，不直接用低层 `KeyDerivation.Pbkdf2` 自定义格式。
- `已验证`：[EF Core SQLite 限制文档](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)说明 `DateTimeOffset` 比较/排序受限并建议持久化 UTC `DateTime`；schema 的时间列仍以 SQLite `TEXT` 保存。
- `已验证`：Claude XHigh challenge 返回 `REVISE`；其 UTC Kind、迁移漂移、用户名原子更新、SQLite CHECK、Token hash 格式、PasswordHasher 配置与 FK 发现经 Codex 独立判断成立。
- `已验证`：EF Core `10.0.10` 默认解析的 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 命中 High [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)；SQLitePCLRaw 官方 `v2.1.12` 维护说明改用原生库 `3.53.3`，项目显式固定该安全版本后漏洞审计清零。

### 假设

- `假设`：v1 登录名限制为 3–64 个 ASCII 字母、数字、点、下划线或连字符；保留原始 `UserName`，唯一 `NormalizedUserName` 使用 invariant uppercase。Unicode 姓名只进入 `DisplayName`，避免 ICU/NLS 与不可见字符造成跨环境身份漂移。
- `假设`：GUID 通过 converter 写为小写标准 `D` 文本；时间通过固定 24 字符 UTC 格式 `yyyy-MM-ddTHH:mm:ss.fffZ` 写入 SQLite `TEXT`，读回标记 `Utc`，SaveChanges 拒绝非 UTC 值。
- `假设`：`PasswordHasher<User>` 固定 IdentityV3/100000 iterations，并由 DI options 构造；包装器用本地枚举保留 rehash-needed，畸形 hash 统一失败。
- `假设`：refresh token 原始值由下一任务生成 32 字节 CSPRNG；本任务固定 `TokenHash=Base64Url(SHA-256(raw bytes))`，43 字符、无 salt/pepper。
- `假设`：应用只注册 DbContext；自动执行迁移、WAL、备份和启动锁属于后续启动/部署任务，本任务不在进程启动时隐式改库。

### 范围

- 必须实现：
  - 为 Server 引入 `Microsoft.EntityFrameworkCore.Sqlite` 与私有设计时 `Microsoft.EntityFrameworkCore.Design`，固定为稳定 `10.0.10`；加入同版本本地 `dotnet-ef` 工具清单，并显式固定无已知漏洞的原生 `SQLitePCLRaw.lib.e_sqlite3 3.53.3`。
  - `User`、`RefreshToken` 服务端实体和 `RelayCoveDbContext`，字段、外键、唯一约束、索引、GUID/UTC 持久化语义与工程方案一致。
  - 仅创建 Users/RefreshTokens 的首个迁移；迁移必须能应用到真实 SQLite 数据库并可回滚。
  - ASCII 用户名规范化服务以及原始名/规范化名原子更新和唯一性；非法输入返回验证失败，不产生 Unicode/控制符异常。
  - Refresh token 确定性 SHA-256 hasher；数据库只存 43 字符 `TokenHash`，不得出现明文 token 列。
  - 基于 ASP.NET Core `IPasswordHasher<User>` 的 `PasswordService`，支持 hash、verify、dummy verify 和 rehash-needed 本地结果，不记录密码或哈希输入；畸形输入不抛出到调用方。
  - 注册 DbContext、PasswordHasher 与 PasswordService；连接串来自配置，开发默认值不得是生产路径。
  - 迁移结构、pending model、up/down、外键/唯一/CHECK 约束、GUID 小写、UTC Kind/比较、密码随机盐、正确/错误/rehash/畸形验证和依赖边界测试。
  - 新增 `DEC-005` 冻结用户名规范化、GUID/时间、密码哈希、refresh token 仅存哈希和迁移应用边界。
- 允许修改：
  - `.config/dotnet-tools.json`
  - `src/RelayCove.Server/**`
  - `tests/RelayCove.Server.Tests/**`
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - `CLAUDE.md`
  - 本任务文件
- 明确不做：
  - 不实现 login/refresh/logout/me Controller，不签发 JWT 或 refresh token，不建立认证 cookie/handler。
  - 不创建默认管理员或管理员创建用户流程，不定义密码复杂度或登录限流。
  - 不创建 Conversations、Messages 等后续表，不在启动时自动执行迁移。
  - 不引入完整 ASP.NET Core Identity schema、Repository/UnitOfWork、第二数据库或内存生产回退。
  - 不记录或提交真实用户名、密码、Token、密钥、数据库文件或连接凭据。

### 验收标准

- [x] 首个迁移只创建 Users、RefreshTokens 和 EF migration history，列类型、外键、唯一键与索引可由 SQLite metadata 验证。
- [x] `NormalizedUserName` 对 ASCII 大小写给出同一结果，数据库拒绝重复规范化名；原始名只能经实体方法更新且非法/不可见/Unicode 登录名被拒绝。
- [x] User/RefreshToken GUID 以小写 `D` 文本保存；时间以固定 UTC 文本保存、读回 `Kind=Utc`，非 UTC 写入失败，LINQ 过期比较正确。
- [x] RefreshTokens 只有 43 字符 `TokenHash`，SHA-256 结果稳定且唯一，非法长度被 CHECK 拒绝，User 删除可清理其 Token。
- [x] 同一密码两次 hash 不同，正确/错误/畸形验证与 dummy verify 不泄漏异常，并以本地结果保留 `SuccessRehashNeeded` 语义。
- [x] Server 运行时依赖不包含 Design 包资产；Shared 不新增依赖；包版本与官方证据一致且漏洞审计无已知漏洞。
- [ ] Claude challenge、Fast、Full、迁移 up/down、文件白名单、`git diff --check` 和候选独立复核通过或按规则如实降级记录。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet tool restore
dotnet ef migrations list --project src/RelayCove.Server --startup-project src/RelayCove.Server
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
if (rg -n '<PackageReference' src/RelayCove.Shared) { throw 'Shared 引入了范围外依赖' }
git diff --check '0e5eefb0c44cdb024e4e455ff91b3eb542adfa8e..HEAD'
```

### 停止并询问

- challenge 或实现证据表明必须改变单 SQLite/EF Core 架构、工程方案的 Users/RefreshTokens 公共语义或认证枚举安全边界。
- 必须存储明文密码/Token、降低密码哈希安全性、在启动时不可控地执行迁移，或必须加入完整 Identity/第二数据库等范围外大型依赖。
- 迁移无法在干净 SQLite 上 up/down，或当前版本存在已知漏洞且没有同主版本安全版本可用。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只实现 Users/RefreshTokens 持久化、首个迁移、用户名规范化和密码哈希，不实现认证端点。
先用仓库与官方证据完成 Claude XHigh challenge，独立判断发现；再固定依赖和 DEC-005。
使用真实 SQLite 迁移和约束测试，不用 EF InMemory 代替关系数据库证据。
Fast 后创建代码检查点，Full 后固定 ReviewHead 做候选复核；不得 push、合并 main 或部署。
```

## 任务结果

### 修改摘要

- 新增 Users/RefreshTokens 实体、EF Core SQLite DbContext、首个 migration 及显式配置注册；启动不自动应用迁移。
- 新增 ASCII 登录名规范化、IdentityV3 密码服务与确定性 refresh-token SHA-256 hasher；所有公共返回避免泄漏框架枚举或原始机密。
- 用真实 SQLite 文件验证 migration up/down、pending model、schema、CHECK/unique/FK/cascade、固定 GUID/UTC 持久化与过期查询。
- 发现并修复 EF 默认传递的 High SQLite 原生依赖漏洞，显式固定 `SQLitePCLRaw.lib.e_sqlite3 3.53.3`。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 0 警告、0 错误；Shared 9 项，Client/Server/Updater 各 1 项，共 12 项通过 |
| `已验证` | Claude challenge | MCP #9/#11 在 300 秒上限无结果，CLI #10 也被外层上限截断；无工具 CLI #12 以实际 `claude-opus-5` / XHigh 完成，费用 `$0.3419475`，结论 `REVISE`；`ChallengeHead=6b821f1e9ba23b005630a3781fd407737e579684` 且调用前后工作树干净 |
| `已验证` | 实现期 Fast | Debug 0 警告、0 错误；Server 28、Shared 9、Client/Updater 各 1，共 40 项测试通过 |
| `已验证` | `dotnet ef migrations list` | 工具构建通过并列出唯一 `InitialAuthenticationStorage` 迁移；未对开发数据库执行更新 |
| `已验证` | 依赖与发布审计 | 原生 SQLite 固定 `3.53.3` 后 8 个项目无已知漏洞；Server publish 的 deps 不含 EF Design，包含安全原生库 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | `ReviewHead=e5ea2174e915cac5a79d152efa2921d223fb3737`：format、Release 构建、40 项测试和 `git diff --check` 通过，0 警告、0 错误 |
| `已验证` | SQLite migration up/down 与模型漂移 | Server 测试在真实临时 SQLite 文件上调用 `MigrateAsync` 与 `IMigrator.MigrateAsync("0")`，表/列、约束、级联和 `HasPendingModelChanges=false` 均通过 |
| `未验证` | 当前分支后台进程烟测 | `Start-Process`/停止脚本被本机命令策略拒绝且未执行；M0 已验证 Server 监听，本任务以 Release 构建、发布与迁移测试覆盖新增路径，且开发 DB 文件不存在 |
| `未验证` | 候选独立复核 | 待固定 ReviewHead 后执行 |

### 文件范围

- 新增：`.config/dotnet-tools.json`，Server `Data/Entities`、`Data/Migrations`、`RelayCoveDbContext`、converter，`Services` 下用户名/密码/token hash 类型，以及四个 Server 测试文件。
- 修改：Server `Program.cs`、项目文件、两个 appsettings；工程方案、`DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md`、`CLAUDE.md` 与本任务记录。
- 删除：无。

### 决策与限制

- 决策：challenge 后采用 ASCII 登录标识、固定 UTC/GUID 文本、hash-only refresh token、显式 IdentityV3 参数和显式迁移应用边界；详见 `DEC-005`。
- 已知限制：本任务不提供任何可调用认证端点，也不证明 Token 签发、会话轮换、WAL/备份或真实 Linux 迁移安全性；当前分支后台进程烟测受命令策略限制未重复执行。

### 下一步

- 实现登录、refresh token 签发/轮换与当前用户端点的最小纵向闭环。
