# M5-01 香港 VPS 与 Windows 双客户端最终 Gate

## 任务定义

- **任务名称：** 阶段 13 内部 RC 真实部署与双客户端验收
- **状态：** `进行中`
- **基准提交：** `c69b98342f1a43dd46056cb73f5ca68eefc99fe6`
- **工作分支：** `agent/stage-13-vps-gate`
- **相关方案章节：** 16、19、阶段 13、21.1–21.5、24；`DEC-054/055/056`

### 目标

在用户指定的香港 Light-A2 协作应用主机部署可恢复的 RelayCove Server 内部 RC，并用两个隔离的真实 Windows Client 实例完成登录、聊天、同步、通知、附件、搜索和更新的可观察端到端验收。只以个人/小团队内部初版可用为目标；发现真正阻断 RC 的缺口时开最小修复切片，不做市场级完善。

### 已知事实

- `已验证`：M4-04 已由 `c69b983` 仅快进合入并推送 `agent/v1-integration`；最终 Fast/Full 1,566/1,566、exact `1.0.0-rc.12` 更新交付 smoke 与多路 Codex 复核通过。
- `已验证`：仓库已有可复现 Linux x64 Server 包、migration bundle、systemd/Nginx/config 模板、Windows self-contained Client ZIP、更新清单生成器和离线 verifier；Server 不在应用启动时自动迁移。
- `已验证`：用户明确授权使用仓库外 `C:\AITemp\Servers_and_Proxies\VPS_应用与代理配置汇总.md` 中“香港 Light-A2 协作应用主机”做实机测试，并预授权绿色 push、仅快进集成、满足 Gate 后的 main/Tag/Release 与部署，无需二次确认。
- `已验证`：配置、凭据、主机地址、密钥、token 和 Authorization 内容不得写入仓库、任务记录、截图文件名或测试日志。

### 假设

- `假设`：目标主机的 SSH、systemd、Nginx、DNS/TLS 或可替代的受控 HTTPS 入口可用；先以只读探针验证，不把假设冒充事实。
- `假设`：两个隔离 Client profile 可在当前 Windows 主机上以两个真实测试账号完成双客户端业务 Gate；若 Windows 单实例键阻止同一登录会话并行，则使用独立 Windows 会话或另一台已授权机器，而不削弱产品单实例约束。

### 范围

- 必须完成：
  - 对指定 VPS 做最小只读盘点，生成并离线验证 exact Server/Client/manifest RC 产物，再按文档执行备份、migration、原子 release 切换、systemd/Nginx/TLS 部署。
  - 验证公网 HTTPS、证书、WebSocket/SignalR、服务重启恢复、上传大小边界、更新 manifest/artifact exact 托管，并确认秘密未进入包或日志。
  - 以两个真实账号和隔离 Client profile 验证登录/持久恢复、文字/回复/@、实时与断线补拉去重、通知/托盘/点击定位、附件上传下载/预览/安全打开、当前/全局搜索与私有撤权。
  - 验证 optional/mandatory 更新说明、下载/hash、失败保留旧版及真实 Client→Updater→新 Client 交接；不得用 safe probe 冒充最终 WPF Gate。
  - 记录脱敏命令、版本、提交、时间、预期/实际、截图和失败恢复证据；必要的小修复须有回归并重新运行相关实机 Gate。
- 允许修改：
  - 为真实部署或自动化所需的 `scripts/`、`installer/`、`docs/` 与最小生产缺口；修改 production 代码时另建可独立验证的小提交并复核。
- 明确不做：
  - 多节点、高可用、Redis/MQ、Kubernetes、自动扩容、公开多租户、移动/Web 客户端、复杂遥测、灰度/增量更新或市场级安装器。
  - 为追求“完美”而重写已绿色的同步、附件、通知或更新架构；未签名/SmartScreen 可作为内部 RC 已知限制，除非它实际阻断受控安装与更新。
  - 在聊天、Git、日志或仓库中暴露任何 VPS/账号/证书私钥信息。

### 验收标准

- [ ] exact Server/Client/manifest RC 产物从干净提交生成并离线验证，产物提交、版本、长度与 hash 有脱敏证据。
- [ ] 指定 VPS 完成真实 migration、systemd、Nginx/TLS、HTTPS/WebSocket、重启恢复与受控备份/恢复检查；公网不直出 Kestrel，秘密不落仓库/产物/普通日志。
- [ ] 两个真实 Client 完成登录、双向消息、断网补拉且不重复、通知/托盘、附件和搜索主路径，并验证私有频道撤权后旧缓存/附件/Toast 不可继续访问。
- [ ] optional 与 mandatory 更新均完成真实 WPF 验收；错误 hash/下载失败不破坏旧版，成功 handoff 后运行 exact 新版且 updater/bootstrap 无残留。
- [ ] 当前 Fast/Full、model drift、八项目漏洞、format/空白以及针对实机发现的回归均通过；Codex 独立复核无剩余阻断 P0/P1。
- [ ] `V1_RC_READY` 证据、明确限制和恢复步骤写入任务/状态/README；只有全部真实满足后才推进 main、Tag/Release 与 `ExecutionStatus=v1_rc_ready`。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
pwsh ./scripts/verify-server-release.ps1 -Version <rc-version>
pwsh ./scripts/verify-client-release.ps1 -Version <rc-version>
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

VPS、双客户端与更新 Gate 的 exact 命令在确认目标环境后写入“任务结果”，其中所有敏感参数只从仓库外配置或进程环境读取，不回显。

### 停止并询问

- 仅当需要删除/覆盖无法恢复的非 RelayCove 数据、变更目标主机其他业务、购买资源/域名/证书、开放额外公网入口，或同一真实阻塞连续满足工作流阈值时停止；用户已授权 RelayCove 范围内的部署、重启、测试数据和绿色发布操作。
- 剩余 Codex 额度低于 15% 时按用户要求中止本任务并保留现场；约每 10 分钟复核一次。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用安全边界。

## 执行提示词

```text
以个人/小团队内部 RC 为尺度，先只读盘点指定 VPS，再复用现有发布链部署 exact 产物并执行双客户端主路径 Gate。三路 Codex 并行盘点，普通审查不调用 Claude；只有新的重大且未解决架构/安全/数据库/协议/可靠性决策才由主代理按一次调用策略处理。秘密不回显、不提交，发现阻断缺口只做最小修复并立即回到实机 Gate。
```

## 任务结果

`进行中`。
