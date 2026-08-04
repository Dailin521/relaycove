<div align="center">

# RelayCove

### 面向小团队的轻量级、自托管 Windows 私域聊天工具

[![Status](https://img.shields.io/badge/status-internal_RC_ready-green.svg)](#项目状态)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

可靠通知 · 断线恢复 · 消息去重 · 本地可控

</div>

## 项目简介

RelayCove 为拥有自有 VPS 的小团队提供一套简单、可维护的私域通信方案。它由 Windows 桌面客户端和轻量服务端组成，支持频道、私聊、附件、搜索、系统通知与断线补拉。

项目优先解决可靠性问题：

- 消息先持久化，再进行实时推送。
- 实时连接中断后持续重连，并补拉遗漏消息。
- 客户端同时对消息和通知进行去重。
- 使用 SQLite 与本地文件存储，降低个人部署和维护成本。
- 保持单体架构，不把大型企业 IM 的复杂基础设施带入第一版。

## 项目状态

> [!IMPORTANT]
> RelayCove 的个人/小团队内部 RC 初版已就绪。登录、频道/私聊、可靠消息、附件、搜索、Windows 通知/托盘、管理员控制面和便携 ZIP 自动更新均已有可运行实现；真实 VPS/TLS、备份恢复、一个真实 WPF Client 与更新升级主链已通过。第二个隔离 Windows UI 全矩阵未执行，属于明确接受的内部 RC 限制，不代表公开发布版本。

当前代码通过统一 Fast/Full 门禁和完整自动化测试。详细范围请阅读 [RelayCove 工程落地方案](./RelayCove_工程落地方案.md)；实时执行状态、已验证证据和仍未完成的实机 Gate 见 [v1 外层执行状态](./docs/ai/V1_EXECUTION.md)。

## 构建与验证

需要 Windows 和 .NET SDK 10.0.101 或同一功能带的更高 patch。仓库统一使用：

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
```

`Fast` 执行还原、Debug 构建和全部测试；`Full` 额外验证格式，执行 Release 构建、全部测试和 Git 空白检查。脚本任一底层命令失败都会非零退出。

## 第一版功能

- 公共频道、私有频道与一对一私聊
- 文字、图片和文件附件
- 回复、`@用户`、链接识别与聊天记录搜索
- Windows 系统通知、提示音、任务栏闪烁与托盘未读数
- 本地消息缓存、断线重连、遗漏消息补拉和消息去重
- 管理员预创建账号、频道管理与基础服务器状态
- 简化的 Windows 客户端自动更新
- VPS 单机部署、HTTPS、日志与服务自动重启

第一版不计划加入语音/视频通话、移动端、Web 客户端、端到端加密、多租户、微服务、消息队列或 Kubernetes。

## 技术栈

| 模块 | 计划技术 |
| --- | --- |
| Windows 客户端 | WPF、.NET 10、CommunityToolkit.Mvvm |
| 服务端 | ASP.NET Core、SignalR、Entity Framework Core |
| 数据存储 | 服务端 SQLite、客户端 SQLite |
| 附件存储 | VPS 本地目录 |
| 通知与驻留 | Windows App Notifications、NotifyIcon、FlashWindowEx |
| 部署 | Linux VPS、Nginx、systemd |
| 日志 | Serilog 或 Microsoft.Extensions.Logging |

## 可靠消息模型

```text
Windows Client
    │
    ├── HTTPS API：登录、写入消息、历史、搜索、附件
    │
    └── SignalR：实时消息和状态事件
            │
            ▼
ASP.NET Core Server
    │
    ├── SQLite：消息先入库，ClientMessageId 保证幂等
    └── 本地目录：附件持久化

断线恢复：重连成功 → 按游标补拉 → 本地入库去重 → 刷新 UI/通知
```

SignalR 只承担实时推送，不作为唯一可靠消息来源。发送、补拉和本地缓存共同构成完整的消息闭环。

## 仓库结构

```text
relaycove/
├── src/
│   ├── RelayCove.Client/
│   ├── RelayCove.Server/
│   ├── RelayCove.Shared/
│   └── RelayCove.Updater/
├── tests/
│   ├── RelayCove.Client.Tests/
│   ├── RelayCove.Server.Tests/
│   ├── RelayCove.Shared.Tests/
│   └── RelayCove.Updater.Tests/
├── docs/
├── scripts/
├── installer/
└── RelayCove.sln
```

## 开发路线

- [x] 明确产品边界、技术栈和可靠性原则
- [x] 完成工程落地方案
- [x] 初始化 .NET 解决方案、基础项目与真实验证脚本
- [x] 定义共享协议、服务端数据库与认证
- [x] 实现会话、消息入库与历史消息
- [x] 打通 SignalR、本地缓存、断线补拉与去重
- [x] 完成 Windows 通知、托盘和任务栏闪烁闭环
- [x] 完成聊天 UI、附件与搜索
- [x] 完成管理员功能与便携 ZIP 自动更新
- [x] 完成真实 VPS/TLS 部署与内部 RC M5 Gate（严格双 Windows UI 矩阵作为已知限制保留）

开发顺序以“消息不丢、通知可靠”为第一优先级，界面与体验优化将在可靠闭环稳定后推进。

## 设计文档

- [工程落地方案](./RelayCove_工程落地方案.md)：第一版范围、总体架构、协议、数据库、可靠性、部署、阶段拆分和验收标准
- [v1 外层执行状态](./docs/ai/V1_EXECUTION.md)：当前里程碑、活动任务、绿色集成头、阻塞和用户 Gate

后续实现过程中将逐步拆分独立的架构、API、数据库、部署和更新文档。

## 参与项目

项目仍在早期阶段，欢迎通过 Issue 讨论需求、架构取舍和实现建议。提交代码前，请优先遵循工程落地方案中的阶段边界与“禁止过度设计”原则。

## License

[MIT](LICENSE)
