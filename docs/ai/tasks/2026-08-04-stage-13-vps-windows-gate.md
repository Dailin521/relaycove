# M5-01 香港 VPS 与 Windows 双客户端最终 Gate

## 任务定义

- **任务名称：** 阶段 13 内部 RC 真实部署与双客户端验收
- **状态：** `已完成（个人/小团队内部 RC 初版；严格双 Windows UI 矩阵按 owner 指令降级为已知限制）`
- **基准提交：** `f0c86009d839e30939976e361135c67560664bcc`
- **工作分支：** `agent/stage-13-vps-gate`
- **相关方案章节：** 16、19、阶段 13、21.1–21.5、24；`DEC-054/055/056`

### 目标

在用户指定的香港 Light-A2 协作应用主机部署可恢复的 RelayCove Server 内部 RC，并用两个隔离的真实 Windows Client 实例完成登录、聊天、同步、通知、附件、搜索和更新的可观察端到端验收。只以个人/小团队内部初版可用为目标；发现真正阻断 RC 的缺口时开最小修复切片，不做市场级完善。

### 已知事实

- `已验证`：阶段 11 已由 `f0c8600` 仅快进合入并推送 `agent/v1-integration`；最终 Fast/Full 1,591/1,591，管理面三路 Codex 复核无 P0/P1。
- `已验证`：仓库已有可复现 Linux x64 Server 包、migration bundle、systemd/Nginx/config 模板、Windows self-contained Client ZIP、更新清单生成器和离线 verifier；Server 不在应用启动时自动迁移。
- `已验证`：用户明确授权使用仓库外 `C:\AITemp\Servers_and_Proxies\VPS_应用与代理配置汇总.md` 中“香港 Light-A2 协作应用主机”做实机测试，并预授权绿色 push、仅快进集成、满足 Gate 后的 main/Tag/Release 与部署，无需二次确认。
- `已验证`：配置、凭据、主机地址、密钥、token 和 Authorization 内容不得写入仓库、任务记录、截图文件名或测试日志。
- `已验证`：目标主机为 Ubuntu 22.04 x86_64，Nginx 1.18 配置有效且运行中，RelayCove 尚未部署、回环端口 5080 未占用，资源满足内部 RC；SSH host key 与既有记录匹配。
- `已验证`：专用 RelayCove DNS 尚不存在；已批准的既有 HTTPS 入口证书仍有效且 `/relaycove/` 前缀未占用，因此本次以该受控子路径部署，保留原有 location，并在每次 reload 前执行 `nginx -t`。
- `已验证`：`2cd7376/e200da6` 已关闭 Hub 查询 token 进入 Nginx access log、服务写更新目录、半成品备份发布和恢复前破坏现状等风险；ReleaseTemplateTests 8/8、WSL 空状态备份恢复及安全/运维两路 Codex 最终复核通过。

### 假设

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

- [x] exact Server/Client/manifest RC 产物从干净提交生成并离线验证，产物提交、版本、长度与 hash 有脱敏证据。
- [x] 指定 VPS 完成真实 migration、systemd、Nginx/TLS、HTTPS/SignalR negotiate、重启恢复与受控备份/恢复检查；公网不直出 Kestrel，秘密不落仓库/产物/普通日志。
- [ ] 原始严格 Gate 的两个隔离真实 Windows Client 全矩阵未执行。已完成一个真实 WPF Client、第二真实认证账号/API actor 与服务端持久化/实时接收；按用户明确的个人/小团队 RC 口径接受该偏差，详见“已知限制”。
- [x] optional 与 mandatory 更新均完成真实 WPF 状态验收，成功 handoff 后运行 exact 新版且 updater/bootstrap/backup/staging 无残留；错误 hash/失败保旧由现有自动化回归覆盖，未在公网链路蓄意制造损坏。
- [x] 当前 Fast/Full、model drift、八项目漏洞、format/空白以及针对实机发现的回归均通过；Codex 独立复核无剩余阻断 P0/P1。
- [x] `V1_RC_READY` 的内部 RC 初版证据、明确限制和恢复步骤已写入任务/状态/README；是否推进公开 Release 仍与“可自用内部 RC”分离。

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

`已完成（内部 RC 初版）`。

### 产物与自动化

- 最终代码头 `93754770eae17588c6e48d5dc3c93cbfdf345442` 包含部署加固、自包含发布、Linux checksum 换行和反向代理子路径更新下载修复；完整提交链为 `2cd7376/e200da6/7f42fe6/b775abe/68fa91d/9375477`。
- Server `1.0.0-rc.15` 来自 `9375477`，大小 `111005704` bytes，SHA-256 `8b3bd1f4e9a054dc7ade0d8f87356dccd6c872749749e1daf3a19a9d18e56b33`；runtimeconfig 已验证为 Linux x64 self-contained。
- Client `1.0.0-rc.14` 来自 `68fa91d`，大小 `165703520` bytes，SHA-256 `7750719358125175f1bd3820d8ac8caa33b741396cfb5b4b31beaec64ac6a6fd`；后续 `9375477` 仅修改 Server 更新路径验证与测试，因此此 Client 仍是对应部署的 exact 产物。optional/mandatory manifest 均指向该精确长度与 hash。
- 最终 Full 1,598/1,598（Shared 69、Server 340、Client 1,151、Updater 38），Release 0 警告/0 错误；ReleaseTemplate 8/8、ServerReleasePackage 1/1、UpdateEndpoint 19/19、model drift、八项目漏洞、format 与空白检查通过。
- Claude #85 MCP 0.5 Sonnet/High 只读挑战的成立意见已在 `e200da6` 落地；最终代理路径修复另由 Codex 安全 reviewer 固定差异复核，结论 PASS、无 P0/P1。

### 真实 VPS 与恢复

- 已在批准的 HTTPS 子路径完成真实 migration、原子 release 切换、systemd 与 Nginx 部署；原站点根响应保持，Nginx 配置校验通过，服务重启后 active，管理员与测试账号可登录，非管理员管理请求为 403，bootstrap 已关闭。
- Kestrel 仅监听 loopback，公网端口不可达；更新目录为 `root:relaycove` 且服务命名空间只读，服务账号直接写入被拒绝。SignalR negotiate 成功，查询 token 未进入普通访问日志。
- 公网下载完整 Client artifact 后重新计算的长度与 SHA-256 精确匹配 manifest；服务端更新端点在真实反向代理 path base 下通过。
- 已执行含真实数据库与上传目录的停服备份/恢复演练：源备份和恢复前备份均完成 hash 验证，恢复后数据库 hash 与源备份一致，演练 sentinel 消失、restore hold 保留，服务恢复 active。

### 真实 Windows Client

- 一个真实 WPF Client 以 DPAPI 凭据恢复登录，显示实时已连接、同步完成、账号就绪与更新为最新；第二真实认证账号由 API actor 发送消息，WPF 无刷新收到并更新会话预览，证明公网 HTTPS→持久化→SignalR→真实 UI 主链可用。
- exact rc.12 在 optional manifest 下真实显示可更新、说明与下载状态；可信 rc.14 ZIP 经客户端校验后显示等待安装，官方包内 Updater 完成原地替换和重启，最终运行 rc.14，未留下 updater/backup/quarantine/staging 残留。
- exact rc.12 在 mandatory manifest 下真实显示强制更新模态框，服务器/账号/密码/登录控件被禁用；验证后远端 manifest 已恢复 optional。最终 rc.14 WPF 保持打开，状态为已连接、已同步、账号就绪、更新最新。

### 已知限制与 owner waiver

- 当前 Windows 会话无管理员令牌且产品有同用户单实例锁，因此没有运行第二个隔离 Windows UI。第二 actor 未持有独立 SignalR 客户端，也未在 VPS 上逐项重跑断网补拉、Toast/托盘点击、附件/搜索 UI 与私有撤权矩阵；这些路径已有自动化、真实本地 Kestrel/WPF seam 与此前阶段证据，但不冒充本次双 WPF 实测。
- 用户已明确要求以个人/小团队内部 RC 为尺度、避免市场级严谨并加速交付；据此将上述差异作为已知限制接受，不再阻断 `V1_RC_READY` 初版。未签名便携 ZIP/SmartScreen、单 VPS/SQLite、无 HA 仍是预期范围限制。
