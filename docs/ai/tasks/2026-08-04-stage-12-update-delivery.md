# M4-04 更新托管、下载与客户端交接

## 任务定义

- **任务名称：** 阶段 12 内部 RC 更新交付闭环
- **状态：** `已完成`
- **基准提交：** `6444656cc966780cf1fea7ee77beab5940e79f66`
- **工作分支：** `agent/stage-12-update-delivery`
- **相关方案章节：** 3.5、4、16、19、阶段 12/13、21.5；`DEC-055`

### 目标

让已交付的 portable ZIP Updater 真正可由应用使用：Server 只读托管当前内部 RC 清单和其精确 ZIP，Client 在启动和用户手动请求时检查更新、显示版本/说明、流式下载并校验，在 optional/mandatory 规则下交给 Updater 后显式退出。以个人/小团队内部 RC 可用为目标，不扩展到签名安装器或公开市场发布。

### 已知事实

- `已验证`：M4-03 已由 `6444656` 仅快进合入并推送 `agent/v1-integration`；`1.0.0-rc.11` 双 ZIP 字节一致，包内有独立 Updater，真实 rc.6→rc.11 自举替换通过。
- `已验证`：Shared 已固定 schema 1、`internal-rc`、`portable-zip`、严格 SemVer、HTTPS URL、size/hash/mandatory/release notes 及更新决策；本任务不另造并行协议。
- `已验证`：当前 Server 没有 `/api/updates/manifest` 或 ZIP 托管；当前 Client 没有更新 transport/coordinator/UI/下载缓存或 Updater 交接。
- `已验证`：Client 普通关闭只隐藏到托盘，彻底退出已有受控生命周期；更新交接必须走显式 Exit，不能强杀，也不能等待负责等待 Client 的外部 Updater 完成。
- `已验证`：工程方案原文是“完整安装包”，但 `DEC-055` 已接受内部 RC 使用完整 portable ZIP 的有意偏离；安装、签名和公开信任链仍未完成。

### 裁定边界

- Server 的更新读取面默认匿名可访问，使未登录启动检查可用；只暴露配置指向且通过 Shared 验证的当前 manifest 与其中唯一 artifact，不提供目录浏览、任意文件名或写入接口。
- Client 只从当前 Server 基址请求清单；artifact URL 必须继续通过 Shared HTTPS/fail-closed 验证。下载进入账户数据之外的受控更新缓存，以 `.part` 流式写入、精确 size 和 SHA-256 校验后原子发布。
- optional 更新允许稍后处理；当前版本低于 `minimumSupportedVersion` 时进入 mandatory gate，聊天/登录主功能不得继续，只有重试检查、下载更新或退出。
- Updater handoff 使用固定 App 目录、固定 `RelayCove.Updater.exe`、当前版本、目标版本、ZIP size/hash 与当前 PID/启动时间；成功发起后 Client 立即走既有显式 Exit 生命周期，不同步等待外部 updater。

### 范围

- 必须实现：
  - Server 当前更新 manifest 与精确 artifact 的只读 HTTP 托管、配置校验、错误/日志/缓存边界和真实集成测试。
  - Client 严格清单获取、版本决策、受控 streaming 下载/校验/原子发布、取消/重试与敏感日志边界。
  - 启动检查、手动检查、新版本/说明/下载进度/失败重试 UI；mandatory 状态阻止继续使用，optional 可稍后。
  - 固定 Updater 启动参数、显式 Exit 交接、精确自举临时目录清理所有权，以及真实发布目录端到端 smoke。
- 允许修改：
  - `src/RelayCove.Server/`、`src/RelayCove.Client/` 与对应测试项目。
  - 必要的 Shared 更新类型补充，但不得破坏 schema 1/`DEC-055`。
  - `scripts/`、Server/Client 发布配置与更新/部署文档。
  - 必要的 `docs/ai/` 任务、状态与决策来源记录。
- 明确不做：
  - MSI/MSIX/WiX/Inno、代码签名、SmartScreen、Program Files/提权、公开 CDN/对象存储、灰度、多通道、增量包、后台静默安装或复杂健康回滚。
  - 生产发布、真实 VPS/域名/TLS/凭据、真实账号、双客户端业务验收；保留到 M5 Gate。
  - 新数据库表、migration、大型更新框架或新的生产依赖。

### 验收标准

- [x] Server 匿名返回通过 Shared 验证的当前清单，并只下载该清单对应 ZIP；缺失、超限、非法清单、路径逃逸和非当前 artifact fail closed。
- [x] Client 启动/手动检查能正确显示无更新、optional、mandatory、错误与 release notes；mandatory 时不能继续使用旧版，optional 可稍后。
- [x] 下载全程有界且可取消，只有精确 size/SHA-256 匹配才原子发布；失败、取消、重试不把 `.part` 当成完整包，也不破坏当前 Client。
- [x] Client 以固定结构化参数启动包内 Updater 并走显式 Exit；真实临时/发布目录 smoke 证明旧进程退出、新版启动、失败仍保留可运行旧版，且只清理本次明确拥有的 bootstrap 目录。
- [x] 定向测试、Fast、Full、真实 HTTP/下载/handoff smoke、model drift、依赖漏洞、format/空白和两路 Codex 独立复核通过；签名/VPS/真实登录视觉保持 `未验证`。

### 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Update"
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Update"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须新增公共更新 schema、数据库/migration、大型依赖、任意文件托管、强杀 Client、管理员权限、签名凭据或生产写入才能完成闭环。
- 现有显式 Exit 生命周期无法在不破坏凭据/缓存/通知/单实例所有权的情况下交给 Updater，或 mandatory gate 存在可继续使用旧版的稳定绕过。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md`；用户已授权范围内绿色 push/仅快进合并，无需二次确认。

## 执行提示词

```text
实现已有 DEC-055 的 M4-04 交付层，不另造更新协议。Server 只读托管 exact manifest/artifact；Client 做启动/手动检查、optional/mandatory UI、受控下载和显式 Exit→Updater。保持便携 ZIP、无强杀、无任意路径、无新依赖；自动化与真实发布目录 smoke 同时证明成功和失败边界。普通审查使用 Codex reviewer，子代理不得调用 Claude。
```

## 任务结果

`已完成`。production 提交为 `08417dafc3c88213712a71ed07940c00ea8a1543`；工作分支 HEAD 为 `894028c98f23024ac07df4ab9252115883b2b48b`。

### 已验证

- 最终定向与全量验证均通过：`pwsh ./scripts/verify.ps1 -Mode Fast`、`pwsh ./scripts/verify.ps1 -Mode Full`；共 `1,566` 项测试（Shared `65`、Server `321`、Client `1,142`、Updater `38`）。
- model drift、`dotnet list RelayCove.sln package --vulnerable --include-transitive`、format 与 whitespace（`git diff --check`）均为绿色。
- 安全 smoke 已通过，证据为 `artifacts/update-delivery-smoke/7ac200823c2a442abeb5e64637c7d076/evidence.json`；覆盖真实 HTTP manifest/artifact、受控下载与显式 Exit→Updater handoff 的成功和失败保留旧版边界。
- exact `rc.12` artifact 为 `165675267` bytes，SHA-256 `e623f38cd3df9c71a62d0eb7f4e86f5a6d69457f2fd0b9d044e6c19b80f057e0`；derived safe probe 为 `198575899` bytes，SHA-256 `ed5c87e3b381449c2542f73104e85739bc162a562056c7df0ba3b468c26a8e0b`。
- 两路 Codex 独立复核均为 `PASS`；Claude Second Brain #83 已完成并读取，结论已纳入本地验证裁定。

### 未验证 / 保留到 M5

- 当前清单端点未知或不可达时 Client 为内部 RC 可用性而 fail-open；只有已经验证为 mandatory 的状态阻止继续使用旧版。这是已接受的产品边界，不是公开发布的安全承诺。
- Server 的 snapshot identity 使用状态、长度与 `LastWriteTimeUtc`；同长度、同时间戳的原位替换可能延迟重新 hash，但 Client 最终 SHA-256 仍拒绝不匹配字节，因此残余影响是可用性而不是把错误包交给 Updater。
- safe smoke 启动的是从 exact 下载包派生的无害 WinExe probe，不启动真实 WPF Client；Kestrel 只读了既有 ASP.NET Core Data Protection key，未写 RelayCove 用户配置。真实登录、通知、Narrator 与更新后的 WPF 视觉仍未冒充通过。
- 代码签名、真实 VPS/域名/TLS、真实账号、双客户端与公开发布信任链仍为 `未验证`；本任务没有将其作为内部 RC 更新交付闭环的完成前提。
