# 阶段 2：认证存储与密码哈希

## 任务定义

- **任务名称：** 阶段 2 — 建立用户与 refresh token 持久化安全基线
- **状态：** 进行中
- **基准提交：** `0e5eefb0c44cdb024e4e455ff91b3eb542adfa8e`
- **工作分支：** `agent/stage-2-auth-storage`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 3.2、5.4、7.1、8.1、11.1、11.2、18.4、19.4、阶段 2；`DEC-001`、`DEC-004`

### 目标

建立后续登录端点可直接使用的服务端持久化与密码验证基线：仅包含 Users、RefreshTokens、可运行的首个 EF Core SQLite 迁移和基于 ASP.NET Core `PasswordHasher` 的密码服务。通过真实 SQLite 迁移与约束测试证明 schema、规范化、机密存储和验证行为，不在本任务签发 Token 或暴露 HTTP 接口。

### 已知事实

- `已验证`：工程方案已固定单 ASP.NET Core 服务、单 SQLite 主库、EF Core、Users/RefreshTokens 字段和 `/opt/relaycove/data/relaycove.db` 生产连接串。
- `已验证`：当前 Server 只有 Web 骨架且无外部包；基准 Fast 通过，Debug 0 警告、0 错误，12 项测试通过。
- `已验证`：本机为 .NET SDK `10.0.101` / runtime `10.0.1`；NuGet 官方页在 2026-08-03 显示 EF Core SQLite/Design 最新稳定 `10.0.10`，均以 `net10.0` 为目标。
- `已验证`：Microsoft 官方文档要求新密码登录应用使用 `PasswordHasher`，不直接用低层 `KeyDerivation.Pbkdf2` 自定义格式。
- `已验证`：EF Core SQLite 官方文档说明 `DateTimeOffset` 比较/排序受限并建议持久化 UTC `DateTime`；schema 的时间列仍以 SQLite `TEXT` 保存。

### 假设

- `假设`：保留原始 `UserName`，新增内部 `NormalizedUserName` 作为唯一登录查找键；使用 Unicode Form KC + invariant uppercase 生成，避免仅依赖 SQLite ASCII `NOCASE`。
- `假设`：所有 GUID 通过 converter 写为小写标准 `D` 文本，所有持久化时间使用 UTC `DateTime`；违反 UTC 前提的输入应在进入数据库前被拒绝。
- `假设`：`PasswordHasher<User>` 的自描述版本化格式和 `SuccessRehashNeeded` 足以覆盖本阶段，不自定义迭代次数或哈希协议。
- `假设`：应用只注册 DbContext；自动执行迁移、备份和启动锁属于部署/启动任务，本任务不在进程启动时隐式改库。

### 范围

- 必须实现：
  - 为 Server 引入 `Microsoft.EntityFrameworkCore.Sqlite` 与私有设计时 `Microsoft.EntityFrameworkCore.Design`，固定为稳定 `10.0.10`；加入同版本本地 `dotnet-ef` 工具清单。
  - `User`、`RefreshToken` 服务端实体和 `RelayCoveDbContext`，字段、外键、唯一约束、索引、GUID/UTC 持久化语义与工程方案一致。
  - 仅创建 Users/RefreshTokens 的首个迁移；迁移必须能应用到真实 SQLite 数据库并可回滚。
  - 用户名规范化服务以及原始名/规范化名唯一性；Token 只存 `TokenHash`，不得出现明文 token 列。
  - 基于 ASP.NET Core `IPasswordHasher<User>` 的 `PasswordService`，支持 hash、verify 和 rehash-needed 结果，不记录密码或哈希输入。
  - 注册 DbContext、PasswordHasher 与 PasswordService；连接串来自配置，开发默认值不得是生产路径。
  - 迁移结构、外键/唯一约束、GUID 小写、UTC、密码随机盐、正确/错误验证和依赖边界测试。
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

- [ ] 首个迁移只创建 Users、RefreshTokens 和 EF migration history，列类型、外键、唯一键与索引可由 SQLite metadata 验证。
- [ ] `NormalizedUserName` 对大小写/兼容等价输入给出同一结果，数据库拒绝重复规范化名；原始 `UserName` 可保留用于诊断。
- [ ] User/RefreshToken GUID 以小写 `D` 文本保存；所有时间为 UTC，SQLite 中可稳定比较过期时间。
- [ ] RefreshTokens 只有 `TokenHash`，哈希唯一且 User 删除可清理其 Token；schema 和日志路径无明文 Token/Password 列。
- [ ] 同一密码两次 hash 不同，正确密码验证成功、错误密码失败，并保留 `SuccessRehashNeeded` 语义。
- [ ] Server 运行时依赖不包含 Design 包资产；Shared 不新增依赖；包版本与官方证据一致且漏洞审计无已知漏洞。
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

- 待实施后填写。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 基准 `pwsh ./scripts/verify.ps1 -Mode Fast` | Debug 0 警告、0 错误；Shared 9 项，Client/Server/Updater 各 1 项，共 12 项通过 |
| `未验证` | Claude challenge | 待固定任务元数据后执行 |
| `未验证` | Fast / Full / SQLite 迁移 | 待实现后执行 |
| `未验证` | 候选独立复核 | 待固定 ReviewHead 后执行 |

### 文件范围

- 新增：待填写。
- 修改：待填写。
- 删除：无。

### 决策与限制

- 决策：待 challenge 与实现证据确认后写入 `DEC-005`。
- 已知限制：本任务不提供任何可调用认证端点，也不证明 Token 签发或会话轮换安全性。

### 下一步

- 实现登录、refresh token 签发/轮换与当前用户端点的最小纵向闭环。
