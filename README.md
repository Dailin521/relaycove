# RelayCove

RelayCove 是一个正在重建中的 Windows 聊天客户端。它不再包含自研聊天服务端、代理或 BFF，而是直接连接既有的 Zulip Server，通过 Zulip REST API 与长轮询事件队列完成登录、同步、历史查询和文本消息收发。

当前版本：`2.0.0-alpha.1`。Stage 21 仍是功能壳阶段，不代表原生 MAUI 最终视觉或可公开发布版本；下一阶段采用已冻结 Web UI 作为原生转换参考。

## 冻结范围

- 首发平台：Windows 11 x64。
- 技术栈：.NET 10、.NET MAUI、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite。
- 默认 Realm：`https://hklight.2000521.xyz`，登录页可编辑。
- 兼容门禁：HTTPS、`zulip_feature_level >= 500`、`is_incompatible=false`、邮箱密码认证可用。
- MVP：单账号、邮箱密码登录、频道/话题、单人/群组/自发私信、50 条历史分页、文本消息、已读/未读、实时事件、SQLite 缓存、断线恢复与队列重建。
- 暂不包含：频道管理、附件、反应、主动编辑/删除、搜索、输入状态、在线状态、通知、推送、SSO、多账号、AI、自动更新、安装器和最终 UI。

来自其他 Zulip 客户端的消息编辑、移动和删除事件仍会被动处理，以保持本地缓存正确。

## 工程结构

```text
src/
  RelayCove.App/            MAUI XAML、ViewModel、Windows composition root、SecureStorage
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

依赖方向固定为：`App -> Core/Data/Zulip.Client`，`Data -> Core`，`Zulip.Client -> Core`。Core 不引用 MAUI、SQLite 或 Zulip JSON DTO。

## 本地开发

先安装 .NET 10 SDK 和 Windows MAUI workload：

```powershell
dotnet workload install maui-windows
dotnet restore RelayCove.sln
pwsh ./scripts/verify.ps1 -Mode Fast
```

完整本地验证与 Windows ZIP：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

`Live` 模式会向专用测试频道写入消息，只有在显式提供目标 Realm、两个专用测试账号 API key 和写入确认变量时才运行；缺少任意值都会立即失败。不要在个人账号、生产频道或非隔离 Realm 上运行。

## 安全边界

- 密码只用于 Zulip `/fetch_api_key`，不持久化。
- API key 只进入 Windows `SecureStorage`，不进入 SQLite、日志、异常或发布包。
- HTTP 自动重定向关闭；客户端不会把密码或 Basic Authorization 转发给其他 origin。
- TLS 仅使用系统证书验证，不提供忽略证书错误的发布开关。
- SQLite 是当前 Windows 用户目录中的明文缓存，不是加密业务主库；Zulip Server 始终是事实源。
- 注销会删除凭据并锁定本地缓存；重新以同一 Realm/用户登录后才能再次解锁。
- 非幂等消息发送不自动重试，网络结果不确定时可能产生重复消息，必须由用户显式再次发送。

## 文档

- [完整重建开发计划](RelayCove_Zulip_MAUI_重建开发计划.md)
- [UI 文档与冻结基线](docs/ui/README.md)
- [Chat UI 交互规格](docs/ui/INTERACTION_SPEC.md)
- [Web UI → 文档 → MAUI 开发工作流](docs/ui/DEVELOPMENT_WORKFLOW.md)
- [Stage 22 原生 Chat UI 实施计划](docs/ai/tasks/2026-08-12-stage-22-native-chat-ui.md)
- [目标 Zulip 主机配置索引](E:/GitHubProject/server-admin/servers/zulip-hklight/README.md)
- [Zulip API 文档](https://docs.zulip.com/api/)
- [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml)

RelayCove 采用 MIT License。项目仅调用 Zulip 公共 API，不复制 Zulip 服务端源码、商标或官方客户端素材；Zulip 是 Zulip, Inc. 的商标。
