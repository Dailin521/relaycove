# 阶段 15：服务器网页管理面板

## 任务定义

- **任务名称：** 阶段 15 — 将管理员控制面迁移到服务器网页
- **状态：** `进行中`
- **基准提交：** `bb217e88e6ab4dba73ffc777dac6f604775cc057`
- **工作分支：** `agent/stage-15-web-admin`
- **相关方案章节：** 17 管理员功能设计、阶段 11、19 部署；`DEC-057/058`

### 目标

在现有 ASP.NET Core Server 内提供可从浏览器使用的轻量管理员面板，覆盖账号、频道、私有成员、服务器状态和附件上限维护。真实网页验证通过前保留 Windows 管理入口，避免迁移期间失去管理能力。

### 已知事实

- `已验证`：现有 `/api/admin/*` 与会话管理服务已完整实现业务规则、事务内管理员复核、token 代际撤销和实时撤权发布，可直接复用。
- `已验证`：Server 当前只有 JWT Bearer，没有 Razor Pages、浏览器 Cookie、antiforgery、静态网页或 PathBase 处理。
- `已验证`：生产入口位于 HTTPS 子路径 `/relaycove/`，Nginx 当前剥离此前缀；网页链接、重定向与 Cookie 必须先获得一致的 PathBase。
- `已验证`：任务开始前 `pwsh ./scripts/verify.ps1 -Mode Fast` 通过，八项目 0 警告/0 错误，1,598 项测试全绿。
- `已验证`：重大认证/反向代理边界已由主代理完成一次 Claude Sonnet/High 只读挑战；建议是 Razor Pages、独立 HttpOnly Cookie、antiforgery、认证 scheme 隔离、数据库实时撤权与先保留 WPF 回退入口。

### 假设

- `假设`：个人/小团队内部使用优先于市场级前端体验；单体 Server-rendered 页面足够，不需要 React、Node、SPA 或新的 Web API。

### 范围

- 必须实现：
  - `/admin/` 中文响应式管理页与独立网页登录/退出。
  - 浏览器 Cookie 与桌面 JWT 严格隔离；HttpOnly、Secure、SameSite、antiforgery、登录限流和数据库实时会话撤权。
  - 用户、频道、私有成员、状态和附件上限的功能对等操作，并复用既有服务与提交后撤权发布。
  - 可配置 PathBase 与持久 Data Protection keys；生产 `/relaycove/` 代理和部署模板同步。
  - 真实 SQLite/TestServer 覆盖认证隔离、CSRF、限流、撤权、子路径和关键管理写操作。
- 允许修改：
  - `src/RelayCove.Server/`、`tests/RelayCove.Server.Tests/`、`installer/linux/`、`scripts/`、`docs/`、README 与方案/决策/状态记录。
- 明确不做：
  - 聊天 Web 端、SPA、前端构建链、OAuth/SSO、复杂 RBAC、审计平台、批量管理、图表或多实例配置同步。
  - 本切片内删除 Windows 管理实现；只有网页在香港 VPS 通过实测后才单独移除。

### 验收标准

- [ ] `/relaycove/admin/` 能登录并完成现有全部基础管理功能，普通用户与未登录用户不能进入。
- [ ] 浏览器只持有受限 Cookie；Cookie 不能访问管理 API，Bearer 不能代替网页登录；Cookie flags、Path 与重定向在子路径正确。
- [ ] 缺少/伪造 antiforgery 的写请求失败；网页登录受限流；禁用、退役、改密或 token 代际变化后旧网页会话立即失效。
- [ ] 用户、频道/成员和上传设置至少各有一条真实页面写操作回归；状态页不泄露额外敏感信息。
- [ ] Fast、Full、发布打包、格式/空白与独立认证安全复核通过；香港 VPS HTTPS 子路径完成浏览器实测。

### 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --filter "FullyQualifiedName~WebAdmin"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
pwsh ./scripts/publish-server.ps1 -Version <stage-version> -OutputDirectory <temporary-directory>
git diff --check
```

### 停止并询问

- 需要新的数据库迁移、浏览器保存 bearer/refresh token、开放公网 Kestrel、降低 HTTPS/CSRF/认证隔离，或需要删除现有可用管理入口才能继续。
- 剩余 Codex 额度低于 15% 时按 owner 既有要求保留现场并停止扩大切片。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md`；绿色 push/仅快进集成已获 owner 持续授权。

## 执行提示词

```text
以个人/小团队内部工具为尺度，在现有 Server 内完成薄 Razor Pages 管理面。复用既有服务，不复制管理规则；网页 Cookie 与桌面 JWT 隔离，所有写操作带 antiforgery，生产子路径和会话撤权必须有真实回归。网页实机通过前保留 Windows 管理入口。
```

## 任务结果

`进行中` — 本地实现、自动化和独立安全复核已完成，等待发布香港 VPS 与浏览器实测。

### 修改摘要

- Server 新增 `/admin/login` 与 `/admin` 中文 Razor Pages，覆盖状态、上传上限、用户、频道和私有成员维护。
- 网页使用独立 HttpOnly/Secure/SameSite Strict Cookie、antiforgery、登录限流和逐请求数据库撤权；API/Hub 保持 JWT-only。
- 新增受校验 PathBase、受限 TempData 反馈 Cookie、管理页安全响应头和可持久化 Data Protection keys；所有管理写失败都给出不含敏感信息的明确反馈。
- 生产配置/发布校验支持根路径或 `/relaycove` 子路径，部署文档冻结 Nginx 必须保留前缀的要求。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | WebAdmin + ReleaseTemplate 定向 | 18/18 通过 |
| `已验证` | Server 全量 Debug | 350/350 通过 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | Release 1,608 项、format、`git diff --check` 通过；一次既有 WPF PackagePart 并行加载偶发失败，原样重跑全绿 |
| `已验证` | 两轮独立 Codex 安全复核 | 初审两项 P2（静默失败、遗漏脱敏错误）已修正；复审无 P0–P2 |
| `未验证` | 香港 VPS `/relaycove/admin/` | 等待本任务下一步发布与浏览器实测 |

### 决策与限制

- 决策：网页登录不产生 access/refresh token；浏览器 Cookie 和桌面 JWT 使用不同 scheme，服务端业务与数据库协议不变。
- 已知限制：登录限流应用于整个登录 Razor Page，因此 GET 登录页也占用同一 IP 的登录额度；10 次/分钟默认值对约 20 人内部使用可接受。

### 下一步

- 从本提交构建 Linux `1.0.0-rc.16` Server 包，保留 `/relaycove` 前缀完成可恢复发布并执行真实网页登录/重启/客户端兼容验证。
