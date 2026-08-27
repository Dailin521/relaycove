# RelayCove

RelayCove 是一个直接连接 Zulip Realm 的 Windows 原生 .NET MAUI 客户端。Zulip 是账号、权限、成员、消息和实时事件的唯一事实源；项目不包含自研聊天服务端、代理、BFF 或第二消息后端。

当前正式版本为 [`2.3.0`](https://github.com/Dailin521/relaycove/releases/tag/v2.3.0)，目标平台为 Windows 11 x64，使用 .NET SDK `10.0.400`、MAUI `10.0.20` 和 `win-x64`。`RelayCove.Web` 只保留为历史源码，不再参与产品开发或 Windows 发布。

## 下载

从 [GitHub Releases](https://github.com/Dailin521/relaycove/releases) 下载 `RelayCove-2.3.0-win-x64.zip` 和对应 `.sha256` 文件，校验后解压运行 `RelayCove.App.exe`。

这是自包含、未签名、无安装器的 ZIP。应用关闭后不会接收消息，也不包含后台推送、自动更新、MSIX 或代码签名。

## 当前范围

- 单账号邮箱密码登录、SecureStorage 凭据恢复和 SQLite 离线缓存。
- 微信式统一会话：一对一/self-DM，以及受支持的私有空话题群聊。
- 历史分页、实时消息、已读/未读、文本与附件、引用、reaction、本人编辑/删除、收藏和搜索。
- 完整 Unicode 表情、图片预览/原图下载、消息文本拖选复制。
- Windows 系统通知、任务栏未读、托盘提醒与会话跳转。
- Zulip 官方在线/忙碌/离线显示，以及个人 emoji/text 状态。

公开频道、命名话题、多人私信、SSO、多账号、`@` 候选、后台 push、安装器和签名不属于当前个人 MVP。

## 工程结构

```text
src/RelayCove.App/            MAUI UI、ViewModel、Windows 平台适配
src/RelayCove.Core/           领域模型、用例、reducer、会话状态
src/RelayCove.Zulip.Client/   Zulip REST/事件协议适配
src/RelayCove.Data/           SQLite 缓存、迁移、账号隔离
tests/                        四个普通 xUnit 项目和显式 LiveTests
```

依赖方向固定为 `App -> Core/Data/Zulip.Client`，`Data -> Core`，`Zulip.Client -> Core`。Core 不引用 MAUI、SQLite 或 Zulip JSON DTO。

## 验证与发布

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
```

`Fast` 运行 Debug build 和四个普通测试项目。`Full` 独立运行 Release build/tests、MAUI app 自包含 publish、ZIP 检查和秘密扫描。`Live` 只有在明确提供隔离凭据及真实写入授权时才能运行。

发布 ZIP 只包含应用运行文件、`LICENSE` 和 `THIRD-PARTY-NOTICES.md`，不包含 `docs/`。

## 安全边界

- 密码只用于换取 Zulip API key；API key 只保存到 Windows SecureStorage。
- HTTP 禁用自动重定向，TLS 只使用系统证书校验。
- SQLite 是当前 Windows 用户目录下的明文缓存，不是第二业务主库。
- 非幂等消息或群资料写入不自动重试；结果不确定时由用户确认权威状态。
- 凭据、正文和服务器原始错误不得进入日志、异常、快照或发布包。

## 文档

- [AI 文档索引](docs/ai/README.md)
- [当前产品与架构计划](RelayCove_Zulip_MAUI_重建开发计划.md)
- [当前状态](docs/ai/STATUS.md)
- [开发工作流](docs/ai/WORKFLOW.md)
- [V2 优化计划](docs/ai/tasks/2026-08-25-v2-optimization-plan.md)
- [UI 文档](docs/ui/README.md)
- [版本说明](docs/releases/)

RelayCove 采用 MIT License。
