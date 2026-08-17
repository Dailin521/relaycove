# RelayCove

RelayCove 是一套直接连接既有 Zulip Realm 的双前端客户端：`RelayCove.Web` 是可独立部署的正式 Web 客户端，`RelayCove.App` 是 Windows-first 的原生 .NET MAUI 客户端。现有 Zulip 官方 Web 保留不动；RelayCove 不包含自研聊天服务端、代理、BFF 或第二套消息后端。

当前 MAUI 版本为 `2.0.0-alpha.1`，仓库锁定 .NET SDK `10.0.400`、MAUI `10.0.20` 和 Windows `win-x64`。Stage 22/23/24 的已合并实现构成当前原生基线；两端共享视觉 Token、交互规格、功能矩阵和验收场景，但不共享 UI 运行时代码。

## 冻结范围

- 前端：TypeScript、React、Vite；Windows：.NET 10、.NET MAUI、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite。
- 默认 Realm：`https://hklight.2000521.xyz`，登录页可编辑。
- 兼容门禁：HTTPS、`zulip_feature_level >= 500`、`is_incompatible=false`、邮箱密码认证可用。
- MVP：单账号、邮箱密码登录、频道/话题、单人/群组/自发私信、50 条历史分页、文本消息、已读/未读、实时事件、SQLite 缓存、断线恢复与队列重建。
- 当前 MAUI 已包含附件、reaction、本人编辑/删除、收藏、服务器搜索/saved、已知用户新会话和普通用户频道自助能力。
- 暂不包含：完整成员关系、`@` 候选、presence、管理员频道管理、通知、推送、SSO、多账号、AI、自动更新、安装器和最终 clean-VM 验收。

来自其他 Zulip 客户端的消息编辑、移动和删除事件仍会被动处理，以保持本地缓存正确。

## 工程结构

```text
src/
  RelayCove.App/            MAUI XAML、ViewModel、Windows composition root、SecureStorage
  RelayCove.Web/            React/Vite 正式 Web、浏览器 Zulip API/session、Playwright
  RelayCove.Core/           领域模型、用例、reducer、会话与同步状态
  RelayCove.Zulip.Client/   Zulip REST/事件队列薄适配层与 JSON DTO 映射
  RelayCove.Data/           SQLite schema、迁移、账号隔离与 mutation lane
tests/
  RelayCove.Core.Tests/
  RelayCove.Zulip.Client.Tests/
  RelayCove.Data.Tests/
  RelayCove.App.Tests/
  RelayCove.Zulip.LiveTests/  显式启用、默认 fail-closed
```

这五个目录都是 Visual Studio Test Explorer 可发现的 xUnit 测试工程。普通 Fast/Full 只运行前四个；`RelayCove.Zulip.LiveTests` 只能由显式授权的 `-Mode Live` 启动。

MAUI 依赖方向固定为：`App -> Core/Data/Zulip.Client`，`Data -> Core`，`Zulip.Client -> Core`。Core 不引用 MAUI、SQLite 或 Zulip JSON DTO。Web 是独立浏览器工程，不引用 MAUI UI runtime 或把 fixture 混入正式 Zulip 数据层。

## 本地开发

先安装 .NET 10 SDK、Windows MAUI workload，以及符合 `src/RelayCove.Web/package.json` engines 的 Node/npm；首次显式准备 Web 依赖和 Chromium：

```powershell
dotnet workload install maui-windows
dotnet restore RelayCove.sln
cd src/RelayCove.Web
npm ci
npx playwright install chromium
cd ../..
pwsh ./scripts/verify.ps1 -Mode Fast
```

日常 Web UI 可直接双击仓库根目录的 `start-web-dev.cmd`，工具会启动本地 Vite 并打开正式登录入口；fixture 只用于显式自动化模式。需要把大版本同步到服务器供人工验收时，双击 `deploy-web.cmd`；固定入口为 `https://hklight.2000521.xyz/relaycove-web/`。详细发布、回滚和一次性 Nginx provision 见 [`docs/deployment.md`](docs/deployment.md)。日常编辑不会自动上线。

完整本地验证与 Windows ZIP：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

`Live` 模式会向专用测试频道写入消息，只有在显式提供目标 Realm、两个专用测试账号 API key 和写入确认变量时才运行；缺少任意值都会立即失败。不要在个人账号、生产频道或非隔离 Realm 上运行。

## 安全边界

- 密码只用于 Zulip `/fetch_api_key`，不持久化。
- MAUI API key 只进入 Windows `SecureStorage`；Web 默认“记住登录”时按已确认产品策略进入当前浏览器 local storage，取消记住则只进入 session storage。
- Web 注销同时清除两种浏览器存储；API key 不进入 URL、日志、UI、异常或测试快照。
- HTTP 自动重定向关闭；客户端不会把密码或 Basic Authorization 转发给其他 origin。
- TLS 仅使用系统证书验证，不提供忽略证书错误的发布开关。
- SQLite 是当前 Windows 用户目录中的明文缓存，不是加密业务主库；Zulip Server 始终是事实源。
- 注销会删除凭据并锁定本地缓存；重新以同一 Realm/用户登录后才能再次解锁。
- 非幂等消息发送不自动重试，网络结果不确定时可能产生重复消息，必须由用户显式再次发送。

## 文档

- [AI 文档索引与当前 active task](docs/ai/README.md)
- [完整重建开发计划](RelayCove_Zulip_MAUI_重建开发计划.md)
- [UI 文档与冻结基线](docs/ui/README.md)
- [Chat UI 交互规格](docs/ui/INTERACTION_SPEC.md)
- [正式 Web → 交互冻结 → 原生 MAUI 开发工作流](docs/ui/DEVELOPMENT_WORKFLOW.md)
- [Stage 22W / 22M 双前端实施记录](docs/ai/tasks/2026-08-12-stage-22-native-chat-ui.md)
- [目标 Zulip 主机配置索引](E:/GitHubProject/server-admin/servers/zulip-hklight/README.md)
- [Zulip API 文档](https://docs.zulip.com/api/)
- [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml)

RelayCove 采用 MIT License。项目仅调用 Zulip 公共 API，不复制 Zulip 服务端源码、商标或官方客户端素材；Zulip 是 Zulip, Inc. 的商标。
