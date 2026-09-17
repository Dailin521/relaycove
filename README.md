# RichChat

RichChat 是一个直接连接 Zulip Realm 的 Windows 原生 .NET MAUI 客户端。Zulip 是账号、权限、成员、消息和实时事件的唯一事实源；项目不包含自研聊天服务端、代理、BFF 或第二消息后端。

当前版本为 [`1.0.7`](https://github.com/Dailin521/relaycove/releases/tag/v1.0.7)，目标平台为 Windows 11 x64，使用 .NET SDK `10.0.400`、MAUI `10.0.20` 和 `win-x64`。`RelayCove.Web` 只保留为历史源码，不再参与产品开发或 Windows 发布。

本版本沿用 SVN 主线的 `1.0.x` 版本系列，构建号为 `14`；历史 GitHub `2.4.0` 不属于新的更新渠道。运行 `publish-installer.cmd` 会生成当前源码版本的安装包及更新清单。

## 下载

从 [GitHub Releases](https://github.com/Dailin521/relaycove/releases) 下载当前正式版本。`v2.3.0` 是更名前发布的历史包，因此仍使用 `RelayCove-2.3.0-win-x64.zip` 和 `RelayCove.App.exe`；后续版本统一使用 `RichChat-<version>-win-x64.zip` 和 `RichChat.exe`。

GitHub Release 提供自包含、未签名的 ZIP 和 `RichChat-<version>-win-x64-Setup.exe` 中文安装器：默认安装到当前用户的 `%LOCALAPPDATA%\Programs\RichChat`，不需要管理员权限，提供开始菜单、桌面快捷方式和 Windows 卸载入口。升级或卸载前请从托盘右键退出 RichChat；卸载保留已有账号凭据和聊天缓存，如需移除凭据请先在应用中注销。

应用退出后不会接收消息，也不包含后台推送、静默安装更新、MSIX 或代码签名。

在“设置 → 通用”可开启或关闭“启动时检查更新”，默认开启，每个新版只提醒一次。“设置 → 关于”可以手动检查 GitHub 正式 Release、下载更新并打开安装包；下载完成后不会自动安装或重启。下载文件经过大小和 SHA-256 校验，打开安装包前请先处理未发送的草稿。

更新渠道只接受 `Dailin521/relaycove` 正式 Release 中配套的 `update-win-x64.json` 和 `RichChat-<version>-win-x64-Setup.exe`，按递增的 `ApplicationVersion` 构建号判断更新。旧 Release 没有清单时不会提示更新，以免将历史 `2.4.0` 误认作当前 `1.0.x` 源码的新版。安装器打包脚本自动生成清单，并核对 ZIP 内程序的版本与构建号；正式发布时需将清单和安装包上传到同一个 Release，且每次发布必须递增构建号。

## 当前范围

表情面板包含“搜索 / 默认 / 收藏”：默认页仍将组织小表情插入输入框；搜索和收藏中的大图点击后单独发送，保留输入框文字与附件。搜索接入 [ChineseBQB](https://github.com/zhaoolee/ChineseBQB)，按名称搜索，支持空关键词浏览及滚动加载，目录每天刷新，图片使用最多 200 MiB 的本地缓存。

收藏支持导入 PNG/JPEG/WebP/GIF，以及聊天图片右键“收藏为表情”。收藏卡片的“⋯”可改名或移除。图片按内容去重、保存原始副本并按账号隔离；删除导入源文件不会影响收藏，注销不会删除收藏。单张上限 25 MiB，发送还受服务器上传限制约束。GIF 保留原始动图，表情面板悬停预览。首次搜索需要网络；第三方表情内容不随安装包分发。

- 单账号邮箱密码登录、SecureStorage 凭据恢复和 SQLite 离线缓存。
- 微信式统一会话：一对一/self-DM，以及受支持的私有空话题群聊。
- 历史分页、实时消息、已读/未读、文本与附件、引用、reaction、本人编辑/删除、收藏和搜索。
- 完整 Unicode 表情、图片预览/原图下载、消息文本拖选复制。
- Windows 系统通知、任务栏未读、托盘提醒与会话跳转。
- Zulip 官方在线/忙碌/离线显示，以及个人 emoji/text 状态。

公开频道、命名话题、多人私信、SSO、多账号、`@` 候选、后台 push 和签名不属于当前个人 MVP。

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

双击根目录 `publish-installer.cmd`，即可依次还原 Release 依赖、运行 Full 并生成中文 EXE 安装器。需要 PowerShell 7、项目要求的 .NET SDK/MAUI Windows 工作负载及 Inno Setup 6.5.4；命令优先使用本机 `.verify/installer/tools/inno-6.5.4/ISCC.exe`，也会查找 Inno Setup 6 的常用安装目录，可通过环境变量 `RELAYCOVE_ISCC` 指定编译器路径。任何一步失败都会停止并保留错误提示；命令行调用时可加 `--no-pause` 取消结束暂停。

也可以在完成当前源码的 `Full` 后单独生成安装器；脚本校验 ZIP 的 SHA-256，并只打包该 ZIP 的内容：

```powershell
pwsh ./scripts/package-installer.ps1 -IsccPath 'C:\path\to\Inno Setup 6\ISCC.exe'
```

安装器、ZIP 和各自的 `.sha256` 均输出到 `artifacts/package/`。安装器内含 .NET 和 Windows App SDK 运行时，安装本身不下载依赖。`scripts/installer/Languages/ChineseSimplified.isl` 来自 [Inno Setup 6.5.4 的社区简体中文翻译](https://github.com/jrsoftware/issrc/blob/is-6_5_4/Files/Languages/Unofficial/ChineseSimplified.isl)，保留原作者信息。

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

RichChat 采用 MIT License。
