<div align="center">

# RelayCove

### 面向小团队的轻量级、自托管 Windows 私域聊天工具

[![Status](https://img.shields.io/badge/status-planning-orange.svg)](#项目状态)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

可靠通知 · 断线恢复 · 消息去重 · 本地可控

</div>

## 项目简介

RelayCove 为拥有自有 VPS 的小团队提供一套简单、可维护的私域通信方案。它计划由 Windows 桌面客户端和轻量服务端组成，支持频道、私聊、附件、搜索、系统通知与断线补拉。

项目优先解决可靠性问题：

- 消息先持久化，再进行实时推送。
- 实时连接中断后持续重连，并补拉遗漏消息。
- 客户端同时对消息和通知进行去重。
- 使用 SQLite 与本地文件存储，降低个人部署和维护成本。
- 保持单体架构，不把大型企业 IM 的复杂基础设施带入第一版。

## 项目状态

> [!IMPORTANT]
> RelayCove 当前处于工程设计与早期开发阶段，仓库暂不包含可运行的客户端或服务端。

现阶段已完成第一版的范围定义、架构选择、协议草案、数据库设计、可靠性方案、部署方案、开发阶段与验收标准。详细内容请阅读 [RelayCove 工程落地方案](./RelayCove_工程落地方案.md)。

## 计划功能

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

## 计划中的仓库结构

```text
relaycove/
├── src/
│   ├── RelayCove.Client/
│   ├── RelayCove.Server/
│   ├── RelayCove.Shared/
│   └── RelayCove.Updater/
├── tests/
├── docs/
├── scripts/
├── installer/
└── RelayCove.sln
```

## 开发路线

- [x] 明确产品边界、技术栈和可靠性原则
- [x] 完成工程落地方案
- [ ] 初始化 .NET 解决方案与基础项目
- [ ] 定义共享协议、服务端数据库与认证
- [ ] 实现会话、消息入库与历史消息
- [ ] 打通 SignalR、本地缓存、断线补拉与去重
- [ ] 完成 Windows 通知、托盘和任务栏闪烁闭环
- [ ] 完成聊天 UI、附件与搜索
- [ ] 完成管理员功能、自动更新与 VPS 发布

开发顺序以“消息不丢、通知可靠”为第一优先级，界面与体验优化将在可靠闭环稳定后推进。

## 设计文档

- [工程落地方案](./RelayCove_工程落地方案.md)：第一版范围、总体架构、协议、数据库、可靠性、部署、阶段拆分和验收标准

后续实现过程中将逐步拆分独立的架构、API、数据库、部署和更新文档。

## 参与项目

项目仍在早期阶段，欢迎通过 Issue 讨论需求、架构取舍和实现建议。提交代码前，请优先遵循工程落地方案中的阶段边界与“禁止过度设计”原则。

## License

[MIT](LICENSE)
