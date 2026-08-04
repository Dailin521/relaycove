# M4-03 便携 ZIP 更新协议与本机替换核心

## 任务定义

- **任务名称：** M4 内部 RC 便携更新核心纵向切片
- **状态：** `进行中`
- **基准提交：** `1a802d613f1d1a7fd07eda7a657b71efd559efa6`
- **工作分支：** `agent/m4-updater-contract`
- **相关方案章节：** 16、阶段 12、阶段 13；`DEC-055`

### 目标

交付可由下一切片直接接入 Client 的内部 RC 更新核心：共享更新清单与版本比较、确定性更新清单生成器、发布包内的独立 Updater，以及在本机对完整 Client ZIP 做校验、等待退出、同卷替换、失败恢复和重启的真实验证。本切片优先满足个人/小团队可用，不把安装器、签名或市场级发布能力伪装成已经完成。

### 已知事实

- `已验证`：M4-02 已由 `1a802d6` 合入并推送 `agent/v1-integration`，可生成字节一致的 `win-x64` self-contained Client ZIP；包内 `manifest.json` 已逐文件记录长度与 SHA-256。
- `已验证`：Updater 当前只返回 `0`，只有一个程序集名称测试；Server 没有更新清单 endpoint，Client 没有检查、下载或交接 UI。
- `已验证`：Client 普通关闭会隐藏到托盘，只有显式 Exit 才结束进程；Updater 不能把 Close 等同于退出，也不能默认强杀。
- `已验证`：当前没有安装器、代码签名证书或公开发布信任链；M4-02 交付物是内部便携 ZIP。
- `已记录`：本决策唯一一次 Claude Sonnet/High 只读 challenge 为后台任务 `c5c5ab8c`；主线由 Codex 与真实测试裁定，不等待它完成才开始实现。

### 裁定边界

- 更新清单 schema 固定为 `1`，单通道 `internal-rc`，单产物类型 `portable-zip`。版本采用严格、可比较的 SemVer 2.0 子集 `major.minor.patch[-prerelease]`，不接受 build metadata。
- 外层更新清单携带目标版本、最低支持版本、是否强制、HTTPS 下载 URL、精确字节数、lowercase SHA-256 和发布说明；它与 ZIP 内逐文件 `manifest.json` 是两层不同证据。
- Updater 不联网。调用者提供已下载 ZIP、期望大小/hash/版本、当前版本、目标目录和精确进程身份；Updater 必须在目标目录外自举后才可替换。
- 只支持普通用户可写的便携目录。目标、staging、backup 和 journal 位于同一父目录/卷；路径、reparse point、ZIP slip、大小写重复、归档上限、逐文件 hash 和必需入口均 fail closed。
- Updater 有界等待目标 PID，并核对启动时间，不强杀。替换使用 staging 与目录 rename；已移动旧目录但新目录未激活时必须恢复。成功启动新 `RelayCove.Client.exe` 后不做进程健康判定或自动回滚。

### 范围

- 必须实现：
  - `RelayCove.Shared/Updates` 中的更新清单 DTO、严格验证、SemVer 比较与更新决策。
  - `scripts/generate-update-manifest.ps1`：从已验证 Client RC ZIP 生成确定性 UTF-8 更新清单。
  - `RelayCove.Updater`：参数解析、外部自举、ZIP/内层 manifest 校验、安全 staging、精确等待、journal/recovery、目录替换与固定 Client 重启。
  - Client publisher/verifier 将 `RelayCove.Updater.exe` 作为 self-contained single-file x64 工具放进 ZIP，并验证可独立启动和参数失败语义。
  - 自动化与真实临时目录 smoke 覆盖成功升级、hash/路径/PID 拒绝及可恢复替换失败。
- 允许修改：
  - `src/RelayCove.Shared/Updates/`、`src/RelayCove.Updater/`。
  - `tests/RelayCove.Shared.Tests/Updates/`、`tests/RelayCove.Updater.Tests/`、`tests/RelayCove.Client.Tests/Packaging/`。
  - `scripts/publish-client.ps1`、`scripts/verify-client-release.ps1`、`scripts/generate-update-manifest.ps1`。
  - `docs/client-release.md`、`docs/portable-update.md` 和必要的 `docs/ai/` 记录。
- 明确不做：
  - Server 更新 endpoint/static hosting、Client 检查/下载/UI/强制阻断/退出交接；这些进入 M4-04。
  - MSI/MSIX/Inno/WiX 安装器、签名、SmartScreen、提权、Program Files、重启后调度、增量包、多通道、灰度、任意命令执行或复杂/健康检查回滚。
  - VPS、真实账号、生产发布与公开分发。

### 验收标准

- [ ] 更新 DTO/版本/决策和生成器对合法内部 RC 清单稳定输出，对 schema、通道、版本、URL、size/hash 和 downgrade fail closed。
- [ ] Client ZIP 包含可独立运行的 self-contained single-file x64 `RelayCove.Updater.exe`，重复构建仍字节一致。
- [ ] Updater 在临时便携安装目录完成真实 rc 版本升级，旧进程退出后新 Client 被启动；用户数据目录不在替换范围。
- [ ] hash/size/版本/内层 manifest/ZIP 路径或文件 hash 不匹配时目标目录不变；替换中断状态能在下一次运行恢复到完整旧版或完整新版。
- [ ] 定向测试、Fast、Full、双包比较、真实 apply smoke、model drift、依赖漏洞、format/空白和两路 Codex 独立复核通过；Claude challenge 完成后读取并裁定 P0/P1。

### 验证命令

```powershell
dotnet test tests/RelayCove.Shared.Tests/RelayCove.Shared.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Updates"
dotnet test tests/RelayCove.Updater.Tests/RelayCove.Updater.Tests.csproj --configuration Debug
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Packaging"
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.7
pwsh ./scripts/verify-client-release.ps1 -Version 1.0.0-rc.7
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须读取证书、密钥、VPS 凭据或真实用户数据，或必须引入大型安装器/更新框架依赖才能形成最小便携更新闭环。
- 真实替换必须写入 Program Files、要求管理员权限、跨卷非原子复制，或无法在不强杀 Client 的情况下验证退出。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md`；用户已明确授权绿色 push/仅快进合并和小团队 RC 收敛，无需为范围内普通实现二次确认。

## 执行提示词

```text
只交付内部 portable ZIP 的离线更新核心和发布接线。保持 Updater 无网络、无任意命令、无强杀；用真实临时目录证明校验、替换、恢复和重启。Server endpoint 与 Client UI/下载/交接进入 M4-04。普通审查使用 Codex reviewer；子代理不得调用 Claude。
```

## 任务结果

`进行中`。

