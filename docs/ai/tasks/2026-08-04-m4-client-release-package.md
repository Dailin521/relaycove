# M4-02 可复现的 Windows Client 可运行内部 RC 包

## 任务定义

- **任务名称：** M4 Windows Client 可运行 RC 发布纵向切片
- **状态：** `进行中`
- **基准提交：** `a7bade17439b4ff328338f6b2bcdba0170c49355`
- **工作分支：** `agent/m4-client-release-package`
- **相关方案章节：** 3.1、3.5、5.4、16、阶段 13、21.5、24.1；`DEC-029/030/031/032`

### 目标

从干净 checkout 用单一 PowerShell 入口生成显式版本、可重复验证的 `win-x64` Client 内部 RC：包含可直接启动的 unpackaged WPF Client、自包含 .NET/Windows App SDK 运行内容、固定布局的 ZIP、manifest 与 SHA-256。离线验证必须证明包结构、版本、hash、PE 架构、运行时资产和秘密排除，并在当前 Windows 主机从发布目录真实启动主窗口；本切片不伪装成安装器或自动更新。

### 已验证现状

- `已验证`：M4-01 已由 `a7bade1` 仅快进合入并推送 `agent/v1-integration`，最终 Server RC 双包字节一致、Fast/Full 1,445/1,445；原任务分支本地/远端已清理。
- `已验证`：Client 是 `WinExe` + `net10.0-windows10.0.19041.0` WPF/WinForms，`WindowsPackageType=None`，依赖 Microsoft.WindowsAppSDK 2.3.1；当前没有 Client 发布/验证脚本或 Windows 安装材料。
- `已验证`：现有 production Client 已实现 unpackaged Windows App SDK 通知、AppInstance 单实例、托盘和主窗口 lifecycle，并有真实 Release WPF smoke 证据；这些证据尚未覆盖从独立 publish 目录启动。
- `已验证`：Updater 目前只返回 0，仓库没有更新 manifest API、安装器技术、签名证书或发布凭据；阶段 16 的完整安装包更新依赖先形成稳定 Client 交付物。
- `已记录`：Claude #81 仍是上一服务端发布决策的后台只读任务；本切片不新增重大协议、数据库或安全架构决策，普通实现与复核只使用 Codex。

### 裁定边界

- 首个 Client 产物是内部使用的 versioned ZIP，不引入 WiX、Inno Setup、MSIX 或其他安装器依赖，也不声明已安装、已签名、可通过 SmartScreen 或适合公开分发。
- 目标是 `win-x64` self-contained、unpackaged Client；不 trimming、不 single-file、不 ReadyToRun。通过实际 publish 验证 `WindowsAppSDKSelfContained` 和 .NET runtime 资产，不凭项目属性宣称自包含。
- ZIP 根固定为 `RelayCove.Client-<version>-win-x64/`，条目稳定排序并固定元数据；manifest 记录版本、commit、SDK、RID、自包含状态和每个文件的长度/hash。相同 commit/version/SDK/输入双构建必须得到相同归档 hash。
- 包内不得出现 PDB、源码、项目文件、数据库、缓存、日志、DPAPI 凭据、`.env`、secret/key/certificate 或本机绝对路径。真实启动 smoke 只使用临时隔离的 `LOCALAPPDATA`/工作目录，不连接 VPS、不创建真实账号、不发送通知或外部消息。

### 范围

- 必须实现：
  - `scripts/publish-client.ps1`：显式版本、安全 staging、`win-x64` self-contained publish、manifest、确定性 ZIP 与 SHA-256。
  - `scripts/verify-client-release.ps1`：离线校验 sidecar/ZIP/manifest、路径与大小边界、逐文件 hash、PE x64 入口、必需运行时资产、版本和秘密排除；支持两个构建做等价比较。
  - `tests/RelayCove.Client.Tests/Packaging/`：脚本安全、模板/包不变量、损坏输入和重复构建回归。
  - `docs/client-release.md`：内部 RC 解压/运行、未签名边界、数据目录和后续安装/更新 Gate。
  - 从最终 publish 目录真实启动 `RelayCove.Client.exe`，确认主窗口出现、响应、第二实例转交/退出，并在检查后无残留测试进程。
- 允许修改：
  - `scripts/`、`tests/RelayCove.Client.Tests/Packaging/`、`docs/client-release.md` 与必要的 `docs/ai/` 记录。
  - 仅当实际 publish 证明必需时，最小修改 `src/RelayCove.Client/RelayCove.Client.csproj` 的发布属性；不修改业务/UI。
- 明确不做：
  - Updater、更新 DTO/API/manifest、最低版本 UI、安装器、代码签名、时间戳、SmartScreen、Tag/GitHub Release 或生产发布。
  - Server/Linux 包、API/schema/migration/dependency、真实账号/VPS、双客户端或通知交互测试。

### 验收标准

- [ ] 干净 checkout 上单命令只向验证后的 `artifacts/` 精确目录生成 versioned ZIP、SHA-256 和可检查 package root；失败不留下半发布的最终目录。
- [ ] 同一 commit/version/SDK/输入连续两次构建字节一致；ZIP 无绝对路径、`..`、重复/大小写碰撞、reparse/link、源码、PDB、用户数据、秘密或临时文件。
- [ ] Client 是 Windows x64 PE、self-contained，入口和必要的 .NET/Windows App SDK/native runtime 资产存在；manifest 与归档逐文件长度/hash/mode 口径一致。
- [ ] verifier 拒绝损坏 sidecar/archive/manifest、错误版本/RID/commit、非 x64/空入口、缺 runtime、额外敏感文件和源树污染。
- [ ] 最终发布目录真实启动主窗口、响应并保持现有单实例语义，检查结束后无残留测试进程；明确记录未签名 ZIP 不是安装器。
- [ ] Packaging 定向、Fast、最终 Full、model drift、依赖漏洞、format/空白与两路 Codex 复核通过；无 Updater/API/schema/dependency/unrelated 改动。

### 初始验证命令

```powershell
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.1
pwsh ./scripts/verify-client-release.ps1 -Version 1.0.0-rc.1
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Packaging"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 实际 publish 无法在不引入新安装器/运行时依赖的前提下形成可启动的 self-contained unpackaged Client，或必须改变通知/AppInstance/账户数据目录语义。
- 必须读取签名证书、PFX、密码、真实账号、VPS 配置或用户数据，或必须修改 Updater/API/schema/生产依赖才能完成。
- 重复构建不等价且无法由明确版本/SDK/输入解释，或 Windows smoke 会影响当前用户真实 RelayCove 数据/通知状态且无法隔离。

## 执行提示词

```text
阅读 AGENTS、方案 3.1/3.5/5.4/16/阶段13/21.5/24.1、STATUS、V1_EXECUTION 和本任务。
只交付 win-x64 self-contained unpackaged Client 内部 RC ZIP、离线验证和真实 publish 启动 smoke。
不碰 Updater、更新协议、安装器、签名、Server/schema/deps/VPS；不把未签名 ZIP 称为可安装正式包。
先用实际 publish/启动证据确认 Windows App SDK 自包含边界，再冻结脚本断言；两路 Codex 并行实现/复核，Claude 不用于普通代码审查。
```

## 任务结果

`进行中`。实现、双构建、真实 publish 启动、独立复核与交接完成后填写。
