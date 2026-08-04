# M4-01 可复现的 Linux 服务端 RC 发布包与离线验收

## 任务定义

- **任务名称：** M4 首个服务端 RC 发布纵向切片
- **状态：** `已完成`
- **基准提交：** `8d8d5d26451f2e3c8aac9879fd7ed2f8affa00f2`
- **工作分支：** `agent/m4-server-release-package`
- **相关方案章节：** 2.2、3.4、5.2、14、16、19、阶段 13、21.5；`DEC-005/006/007/017/042/043/054`

### 目标

从一个干净 checkout 用单一 PowerShell 入口生成版本化、可重复验证的 `linux-x64` 服务端 RC 产物：包含 production Server、独立 EF migration bundle、确定布局的归档与 SHA-256，以及与当前真实配置键一致的 systemd、Nginx、环境变量/生产配置模板和部署步骤。验证入口必须在不读取真实秘密、不连接 VPS、不修改源目录或生产数据的前提下证明包结构、hash、权限元数据、配置边界和秘密排除。

### 已验证现状

- `已验证`：M3 客户端搜索已在 `8d8d5d2` 仅快进合入本地/远端 `agent/v1-integration`；最终 Fast/Full 1,426/1,426、两路 Codex 复核与 Release WPF lifecycle 通过。
- `已验证`：仓库已有 `scripts/verify.ps1`、固定 .NET SDK `10.0.101`（同 feature band 最新 patch）、deterministic 与 warnings-as-errors，但没有 publish/installer/deployment/CI 材料。
- `已验证`：Server 是 `net10.0` ASP.NET Core + SQLite，已有四个 migration 和 EF Design 包；启动不会自动 `Migrate()`。本机 `dotnet ef migrations bundle --help` 支持 `--self-contained --target-runtime linux-x64`。
- `已验证`：生产真实配置键是 `ConnectionStrings:Default`、`Storage:UploadsPath`、`Uploads:MaximumFileBytes`、`Authentication:*` 与 `BootstrapAdmin:*`；JWT key 必须从仓库外注入、Base64 解码后至少 32 bytes，bootstrap 默认关闭。
- `已验证`：附件文件绝对上限为 100 MiB，请求体另允许 64 KiB multipart overhead；工程方案中的旧 `RelayCove:...` 和 200 MB 示例不能直接成为发布模板。
- `已验证`：Updater 仍为空壳，客户端未冻结 RID/安装器；本切片不把它们伪装成可发布能力。
- `已记录`：发布矩阵/迁移/systemd/Nginx 边界的唯一一次 Claude #81 已以本机 Claude Code 2.1.221 后台持久 Sonnet/High 只读任务 `6798888b` 启动；两个 CLI 参数解析产生的空会话未发送模型任务并已停止，不计为调用。

### 已裁定边界

- Server 与 migration bundle 均以 `linux-x64` self-contained 发布；Server 不 trimming、不 single-file、不 ReadyToRun，migration bundle 按 EF 生产入口交付为单一自包含可执行文件，目标 VPS 不依赖预装 .NET。版本必须由调用方显式提供并写入 assembly/package 名；禁止隐式使用时间作为版本。
- 归档根固定为 `RelayCove.Server-<version>-linux-x64/`，至少包含 `app/`、`migrate/`、`deploy/` 与 manifest。归档外生成小写 SHA-256 sidecar；同一 commit/version/SDK/输入重复构建应得到相同文件清单、逐文件 hash 与归档 hash。
- migration bundle 是生产升级的显式入口，不在 Server 启动时自动迁移。部署步骤必须先停止服务、备份 SQLite 主库及同目录 WAL/SHM 状态、以 `relaycove` 身份执行 bundle、失败则保持服务停止并人工恢复；不得声称自动回滚或多实例在线迁移。
- systemd 以专用非 root `relaycove` 用户运行，仅写 `/var/lib/relaycove`，秘密只从权限受控的 `/etc/relaycove/relaycove.env` 注入；包内只提供无秘密 example。Nginx 终止 HTTPS、代理到 loopback Kestrel、保留 SignalR Upgrade，并把 request body 上限设为至少 `100 MiB + 64 KiB` 且仍由应用执行最终文件限制。
- 发布/验证脚本只允许清理调用者指定且验证位于仓库 `artifacts/` 下的精确版本目录；不得删除或覆盖源目录、仓库外目录、数据库、uploads 或现有非本任务产物。

### 范围

- 必须实现：
  - `scripts/publish-server.ps1`：显式版本、干净 staging、Server + migration bundle、部署材料、manifest、确定性归档与 SHA-256。
  - `scripts/verify-server-release.ps1`：离线验证归档/sidecar、路径白名单、逐文件 hash、Linux executable mode、版本、必需/禁止项、配置键和秘密扫描；支持对两个独立构建做等价比较。
  - `installer/linux/`：systemd unit、Nginx 示例、production JSON 与 env example，全部使用真实配置键且不含秘密。
  - `docs/deployment.md`：准备目录/用户、秘密、备份/迁移、安装、Nginx/TLS、启动、升级、失败恢复和明确 M5 Gate。
  - 对应 Server packaging 自动化测试，覆盖脚本/模板/包不变量和恶意/损坏输入。
- 允许修改：
  - `scripts/`、`installer/linux/`、`docs/deployment.md`
  - `tests/RelayCove.Server.Tests/Packaging/` 与必要的 `docs/ai/` 记录。
  - 独立部署复核确认 Nginx 会让现有认证限流退化为全站共享 loopback 后，允许最小修改 Server Forwarded Headers 管线与对应认证限流回归；不改变 API、业务语义或依赖。
  - 仅当实际产物版本元数据无法由命令行注入时，最小修改 `Directory.Build.props`。
- 明确不做：
  - 真实 VPS、systemd/Nginx/TLS 启动、生产数据库或生产秘密访问。
  - Client/Updater、安装器、更新 manifest API、GitHub Release、Tag、`main` 合并或生产部署。
  - Server 公共协议、业务代码、schema/migration、新生产依赖、自动在线迁移、自动回滚或多实例。

### 验收标准

- [x] 干净 checkout 上单命令生成版本化 `linux-x64` Server、migration bundle、部署材料、manifest、归档与正确 SHA-256；输出只位于验证后的 `artifacts/` 精确目录。
- [x] 同一 commit/version/SDK/输入连续两次构建的包布局、逐文件 hash 和归档 hash 相同；归档无绝对路径、`..`、重复/大小写碰撞、源代码、PDB、数据库、uploads、logs、开发配置或临时文件。
- [x] Server 与 migration bundle 是 Linux x64 self-contained、入口具有 executable mode；manifest 准确记录版本、commit、RID、SDK、文件长度/hash 与唯一相对路径。
- [x] systemd 使用非 root、loopback Kestrel、受控 EnvironmentFile/StateDirectory/UMask 和有界停止/重启策略；模板不存在真实 key/password/token、bootstrap 默认关闭。
- [x] Nginx 只示例 HTTPS 终止与 loopback upstream，支持 SignalR Upgrade，body 上限不早于应用 100 MiB + 64 KiB 边界；配置字段与当前 Options/Program 一致。
- [x] 部署文档明确停止→一致备份→migration bundle→启动顺序、失败保持停止/人工恢复、首次 bootstrap 凭据移除，以及哪些 Linux/VPS/TLS/双客户端事实仍为 `未验证`。
- [x] packaging 定向、Fast、最终 Full、model drift、依赖漏洞、format/空白、秘密扫描和两路独立 Codex 复核完成；Claude #81 已按重大发布决策单次启动且保持后台运行，按用户策略完成后读取但不阻塞主线；无业务/schema/dependency/unrelated 改动。

### 初始验证命令

```powershell
pwsh ./scripts/publish-server.ps1 -Version 1.0.0-rc.1
pwsh ./scripts/verify-server-release.ps1 -Version 1.0.0-rc.1
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Packaging"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 实际 VPS 架构不是 Linux x64，或必须改变 self-contained、SQLite 单实例/迁移、目录所有权、TLS 终止或秘密注入边界才能完成。
- 必须改公共协议、业务代码、schema/migration、生产依赖，或发布脚本需要读取/写入仓库外秘密、数据库、uploads 或生产路径。
- 无法从干净源验证产物，重复构建不等价且原因无法由明确工具链输入解释，或验证必须在 Windows 上伪装 Linux/systemd/Nginx 已真实运行。

## 执行提示词

```text
阅读 AGENTS、方案 14/16/阶段13/21.5、WORKFLOW、STATUS、V1_EXECUTION 和本任务。
只实现服务端 RC 发布包与离线验证；不碰 Client/Updater/API/schema/deps/VPS。
输出只在精确 artifacts 目录；不读取真实秘密，不自动迁移，不声称 Windows 验证了 Linux 运行。
Claude #81 后台只读运行；Codex 三路并行实现/挑战，所有意见由主代理以真实产物复验。
```

## 任务结果

`已完成`。

- 生产提交：`47c6f88e83e822d9225bc358edff128599099596`（可复现 Server/migration bundle、USTAR、manifest/hash、systemd/Nginx/config、部署文档与 Packaging 测试）；`a482d35fd0225feb9a412933fba0d5c6c444a4ed`（loopback 单跳代理头、认证限流分区、迁移失败恢复、秘密与 ELF fail-closed）；`b13188643cfc72c9d0896fc919fe53448e1c7ea5`（三个 self-contained runtime 库及 Nginx 转发头进入离线强制验证）。
- 产物：干净提交 `a482d35` 的两份 `1.0.0-rc.4` 均为 361 文件、`linux-x64` self-contained，Server、migration bundle 与关键 runtime 库为 ELF64 x86-64；两个 110,914,241-byte 归档字节一致，SHA-256 均为 `ed01d0b8ccd026ea648d48a43e83093a25b1dc58ee4a6bc5a947bd9cc3ed1c24`。最新 `b131886` 验证器再次通过该双包比较。
- 自动化：Packaging + 认证代理限流定向 20/20；最终 Fast/Full 均为 1,445/1,445（Shared 41、Server 302、Client 1,101、Updater 1），Release 构建 0 警告/0 错误，PowerShell parser、format 与 `git diff --check` 通过。EF model drift 无变化；8 个项目 direct/transitive 漏洞审计无已知漏洞。
- 复核：运维/安全两路独立 Codex 首轮发现代理限流、迁移 fail-fast/精确恢复、权限、Nginx lifecycle、ELF/runtime 与秘密排除缺口，成立项均修正；最终交叉复审关闭 runtime ELF 与 Nginx 转发头两项 P1，无剩余 P0/P1。Claude #81 本机持久 Sonnet/High 任务 `6798888b` 仍在运行，完成后读取并由主代理按真实 Linux/VPS 边界裁定。
- 边界：Windows 已验证生成、结构、hash、重复构建、配置/秘密与自动化，不声称 Linux、systemd、Nginx/TLS、migration/restore、真实 VPS、真实客户端或双客户端已运行；这些仍是 M5 Gate。Client/Updater、安装器、更新 manifest、Tag/Release 与生产部署未包含在本切片。
