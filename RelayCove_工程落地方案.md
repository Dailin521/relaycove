# RelayCove 工程落地方案

> 本文档用于指导 AI Agent 逐步实现 RelayCove。  
> RelayCove 是一个面向小团队的轻量级、自托管 Windows 私域聊天工具。  
> 第一目标是：**通知可靠、消息不丢、断线可恢复、实现简单、便于个人维护。**

---

## 目录

1. 项目定位
2. 第一版边界
3. 技术栈
4. 总体架构
5. GitHub 仓库与解决方案结构
6. AI Agent 开发原则
7. 核心业务概念
8. 服务端工程设计
9. 客户端工程设计
10. Shared 共享协议设计
11. 数据库设计
12. 消息可靠性设计
13. 通知可靠性设计
14. 附件系统设计
15. 搜索系统设计
16. 自动更新设计
17. 管理员功能设计
18. 日志与故障排查
19. VPS 部署方案
20. 开发阶段拆分
21. 每阶段验收标准
22. 禁止过度设计清单
23. AI Agent 任务模板
24. 最终交付标准

---

# 1. 项目定位

## 1.1 项目名称

```text
RelayCove
```

含义：

- `Relay`：消息可靠传递、补发、转发；
- `Cove`：私密的小海湾，象征小团队自己的通信空间。

## 1.2 项目一句话描述

```text
A lightweight self-hosted chat application for small teams.
```

中文描述：

```text
一个面向小团队的轻量级、自托管 Windows 私域聊天工具。
```

## 1.3 使用场景

RelayCove 面向以下场景：

- 20 人左右的小团队；
- 团队拥有自己的 VPS；
- 主要在 Windows PC 上使用；
- 需要频道和私聊；
- 需要发送文字、图片、文件、视频附件；
- 需要可靠通知；
- 需要友好的聊天记录搜索；
- 不追求大型企业 IM 的复杂功能。

## 1.4 第一优先级

本项目第一优先级不是界面好看，也不是功能丰富，而是：

```text
消息必须入库
通知必须可靠
断线必须重连
漏消息必须补拉
同一消息不能重复显示或重复通知
```

---

# 2. 第一版边界

## 2.1 第一版必须完成

第一版需要达到团队内部日常试用标准。

必须完成：

- Windows 客户端；
- VPS 服务端；
- 用户名密码登录；
- 管理员预创建账号；
- 公共频道；
- 私有频道；
- 一对一私聊；
- 发送文字；
- 支持换行；
- 复制消息；
- 回复消息；
- `@用户`；
- 链接识别；
- 发送图片；
- 发送文件；
- 视频作为普通文件发送；
- 粘贴截图；
- 拖拽上传；
- 图片缩略图；
- 点击查看原图；
- 上传进度；
- 下载进度；
- 上传失败重试；
- 打开文件；
- 打开文件所在目录；
- 历史消息；
- 当前会话搜索；
- 全局搜索；
- 中文部分匹配；
- 搜索附件文件名；
- Windows 系统通知；
- 通知点击跳转；
- 提示音；
- 任务栏闪烁；
- 托盘常驻；
- 托盘未读数；
- 开机启动；
- 关闭窗口进入托盘；
- 单实例运行；
- 断线持续重连；
- 断线消息补拉；
- 消息去重；
- 通知去重；
- 基础未读数；
- 管理员页面；
- 简化自动更新；
- 客户端日志；
- 服务端日志；
- HTTPS；
- 服务异常自动重启。

## 2.2 第一版暂不做

第一版不做：

- 语音通话；
- 视频通话；
- 朋友圈；
- 好友申请；
- 复杂权限系统；
- 复杂组织架构；
- 端到端加密；
- 多租户；
- 多服务器集群；
- Redis；
- MQ；
- Elasticsearch；
- MinIO；
- Docker 编排；
- Kubernetes；
- 移动端；
- Web 客户端；
- 在线文档；
- 插件市场；
- 复杂机器人平台；
- 复杂自动更新灰度；
- 增量更新；
- 自动回滚；
- 自动备份恢复界面。

---

# 3. 技术栈

## 3.1 客户端

```text
WPF
.NET 10
CommunityToolkit.Mvvm
Microsoft.AspNetCore.SignalR.Client
Microsoft.Data.Sqlite
Windows App Notifications
Windows Forms NotifyIcon 或等价托盘方案
FlashWindowEx
DPAPI 本地加密
Serilog 或 Microsoft.Extensions.Logging
```

## 3.2 服务端

```text
ASP.NET Core
SignalR
Entity Framework Core
SQLite
Serilog
Nginx
systemd
```

## 3.3 数据库

```text
服务端：SQLite
客户端：SQLite
```

## 3.4 附件存储

```text
VPS 本地目录
```

示例：

```text
/opt/relaycove/data/uploads
```

## 3.5 自动更新

第一版使用完整安装包更新：

```text
更新清单 JSON
完整安装包下载
SHA-256 校验
启动安装器
关闭旧客户端
安装后重新启动
```

---

# 4. 总体架构

## 4.1 架构图

```text
Windows WPF Client
    |
    | HTTPS REST API
    | - 登录
    | - 发消息
    | - 拉历史
    | - 搜索
    | - 上传附件
    | - 下载附件
    | - 检查更新
    |
    | SignalR
    | - 实时消息推送
    | - 会话变更推送
    | - 在线状态推送
    |
    v

ASP.NET Core Server
    |
    | EF Core
    v

SQLite Database

    |
    v

Local Uploads Directory
```

## 4.2 关键原则

### 消息发送

消息发送使用 HTTP API。

原因：

- 便于幂等；
- 便于重试；
- 便于记录日志；
- 便于和 SignalR 连接状态解耦。

流程：

```text
客户端 POST /api/messages
服务端验证权限
服务端写入 SQLite
服务端返回 MessageDto
服务端通过 SignalR 推送 NewMessage
客户端本地入库
客户端刷新 UI
```

### 实时推送

SignalR 只负责实时通知和事件推送。

SignalR 不作为唯一可靠消息来源。

### 断线恢复

客户端必须记录最后同步到的服务端消息 ID 或时间游标。

重连成功后调用：

```text
GET /api/sync?afterMessageId=xxx
```

补拉遗漏消息。

---

# 5. GitHub 仓库与解决方案结构

## 5.1 仓库名

```text
relaycove
```

## 5.2 解决方案名

```text
RelayCove.sln
```

## 5.3 顶层目录结构

```text
relaycove/
├── src/
│   ├── RelayCove.Client/
│   ├── RelayCove.Server/
│   ├── RelayCove.Shared/
│   └── RelayCove.Updater/
│
├── tests/
│   ├── RelayCove.Server.Tests/
│   ├── RelayCove.Client.Tests/
│   └── RelayCove.Shared.Tests/
│
├── docs/
│   ├── architecture.md
│   ├── api.md
│   ├── database.md
│   ├── deployment.md
│   ├── update.md
│   └── ai-agent-rules.md
│
├── scripts/
│   ├── publish-client.ps1
│   ├── publish-server.sh
│   ├── deploy-server.sh
│   └── create-update-manifest.ps1
│
├── installer/
│   └── windows/
│
├── .github/
│   └── workflows/
│
├── README.md
├── LICENSE
├── .gitignore
└── RelayCove.sln
```

## 5.4 项目职责

### RelayCove.Shared

只放共享内容：

- DTO；
- API 请求和响应模型；
- 枚举；
- 常量；
- 协议版本；
- 错误码；
- 简单工具类。

不要放：

- 服务端数据库实体；
- 客户端 ViewModel；
- UI 代码；
- 服务端业务逻辑。

### RelayCove.Server

负责：

- 登录认证；
- 用户管理；
- 频道管理；
- 私聊会话；
- 消息保存；
- 消息推送；
- 消息补拉；
- 附件上传下载；
- 搜索；
- 管理员接口；
- 更新清单接口；
- 日志；
- 数据库迁移。

### RelayCove.Client

负责：

- WPF UI；
- 登录界面；
- 会话列表；
- 聊天窗口；
- 附件上传下载；
- 本地缓存；
- 消息同步；
- SignalR 连接；
- Windows 通知；
- 任务栏闪烁；
- 托盘；
- 开机启动；
- 自动更新检查。

### RelayCove.Updater

负责：

- 接收安装包路径；
- 等待主程序退出；
- 启动安装包；
- 必要时重启主程序。

第一版 Updater 保持极简，不实现复杂回滚。

---

# 6. AI Agent 开发原则

## 6.1 总原则

AI Agent 必须按阶段开发，不允许一次性生成完整项目。

每个阶段必须满足：

- 能编译；
- 能运行；
- 有日志；
- 有最小验收步骤；
- 不引入未要求的复杂依赖。

## 6.2 禁止擅自扩展

AI Agent 不得擅自引入：

- Redis；
- RabbitMQ；
- Kafka；
- Elasticsearch；
- MinIO；
- Docker Compose 作为第一依赖；
- Kubernetes；
- Clean Architecture 过度分层；
- CQRS；
- Event Sourcing；
- DDD 聚合根；
- 微服务；
- 复杂插件系统；
- GraphQL；
- gRPC；
- 自定义加密协议；
- 前后端分离后台管理站点。

## 6.3 代码风格

必须遵守：

- C# 开启 Nullable；
- 异步 API 使用 `async/await`；
- UI 线程不做耗时操作；
- 网络、文件、数据库操作必须捕获并记录异常；
- 服务端接口必须校验认证；
- 服务端接口必须校验会话权限；
- 附件路径必须防止目录穿越；
- 消息写入必须幂等；
- 客户端消息展示必须去重；
- 客户端通知必须去重；
- 所有重要流程必须有日志。

## 6.4 每次任务输出要求

AI Agent 每完成一个模块，必须输出：

```text
1. 修改了哪些文件
2. 新增了哪些文件
3. 如何运行
4. 如何验证
5. 已知限制
6. 下一步建议
```

---

# 7. 核心业务概念

## 7.1 User

用户。

用户由管理员创建，不开放注册。

字段包括：

- 用户 ID；
- 用户名；
- 昵称；
- 头像；
- 密码哈希；
- 是否管理员；
- 是否禁用；
- 创建时间；
- 更新时间；
- 最后登录时间；
- 最后在线时间。

## 7.2 Conversation

会话。

会话类型：

```text
PublicChannel
PrivateChannel
Direct
```

说明：

- 公共频道：所有正常用户可见；
- 私有频道：只有成员可见；
- 私聊：两个用户之间的一对一会话。

## 7.3 ConversationMember

会话成员。

用于记录：

- 某用户是否属于某会话；
- 是否静音；
- 最后已读消息；
- 未读计数；
- 是否管理员；
- 加入时间。

第一版可以只做必要字段。

## 7.4 Message

消息。

消息类型：

```text
Text
Image
File
System
```

第一版视频使用 `File` 类型。

## 7.5 Attachment

附件。

附件可属于一条消息。

一条消息可以有多个附件。

附件信息包括：

- 原始文件名；
- 服务器存储文件名；
- 大小；
- MIME 类型；
- 上传者；
- 所属消息；
- 上传时间。

## 7.6 LocalMessage

客户端本地消息缓存。

用于：

- 快速打开历史；
- 断线后补拉去重；
- 控制通知是否已经弹出；
- 控制未读状态；
- 搜索本地缓存。

---

# 8. 服务端工程设计

## 8.1 服务端目录结构

```text
RelayCove.Server/
├── Controllers/
│   ├── AuthController.cs
│   ├── UsersController.cs
│   ├── ConversationsController.cs
│   ├── MessagesController.cs
│   ├── AttachmentsController.cs
│   ├── AdminController.cs
│   └── UpdatesController.cs
│
├── Hubs/
│   └── ChatHub.cs
│
├── Data/
│   ├── RelayCoveDbContext.cs
│   ├── Entities/
│   └── Migrations/
│
├── Services/
│   ├── AuthService.cs
│   ├── PasswordService.cs
│   ├── TokenService.cs
│   ├── UserService.cs
│   ├── ConversationService.cs
│   ├── MessageService.cs
│   ├── AttachmentService.cs
│   ├── SearchService.cs
│   ├── UpdateService.cs
│   └── ServerStatusService.cs
│
├── Options/
│   ├── AuthOptions.cs
│   ├── StorageOptions.cs
│   ├── UploadOptions.cs
│   └── UpdateOptions.cs
│
├── Middleware/
│   └── ErrorHandlingMiddleware.cs
│
├── Program.cs
├── appsettings.json
└── appsettings.Production.json
```

## 8.2 服务端接口分类

### 认证接口

```text
POST /api/auth/login
POST /api/auth/refresh
POST /api/auth/logout
GET  /api/auth/me
```

### 用户接口

```text
GET    /api/users
GET    /api/users/{id}
PUT    /api/users/me/profile
PUT    /api/users/me/password
POST   /api/users/me/avatar
```

### 会话接口

```text
GET    /api/conversations
GET    /api/conversations/{id}
POST   /api/conversations
PUT    /api/conversations/{id}
DELETE /api/conversations/{id}
GET    /api/conversations/{id}/members
POST   /api/conversations/{id}/members
DELETE /api/conversations/{id}/members/{userId}
```

### 消息接口

```text
GET  /api/conversations/{conversationId}/messages?beforeMessageId=xxx&limit=50
POST /api/messages
GET  /api/sync?afterMessageId=xxx
POST /api/conversations/{conversationId}/read
GET  /api/search?keyword=xxx&conversationId=optional
```

### 附件接口

```text
POST /api/attachments
GET  /api/attachments/{id}
GET  /api/attachments/{id}/download
```

### 管理员接口

```text
POST   /api/admin/users
PUT    /api/admin/users/{id}
DELETE /api/admin/users/{id}
POST   /api/admin/users/{id}/reset-password
GET    /api/admin/status
PUT    /api/admin/settings/upload
```

### 更新接口

```text
GET /api/updates/manifest
```

---

# 9. 客户端工程设计

## 9.1 客户端目录结构

```text
RelayCove.Client/
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
│
├── Views/
│   ├── LoginView.xaml
│   ├── MainShellView.xaml
│   ├── ConversationListView.xaml
│   ├── ChatView.xaml
│   ├── MessageListView.xaml
│   ├── MessageInputView.xaml
│   ├── SearchView.xaml
│   ├── SettingsView.xaml
│   ├── AdminView.xaml
│   └── UpdateDialog.xaml
│
├── ViewModels/
│   ├── LoginViewModel.cs
│   ├── MainShellViewModel.cs
│   ├── ConversationListViewModel.cs
│   ├── ChatViewModel.cs
│   ├── MessageItemViewModel.cs
│   ├── MessageInputViewModel.cs
│   ├── SearchViewModel.cs
│   ├── SettingsViewModel.cs
│   ├── AdminViewModel.cs
│   └── UpdateViewModel.cs
│
├── Services/
│   ├── ApiClient.cs
│   ├── AuthClient.cs
│   ├── ChatConnectionService.cs
│   ├── MessageSyncService.cs
│   ├── LocalDatabaseService.cs
│   ├── NotificationService.cs
│   ├── TaskbarFlashService.cs
│   ├── TrayService.cs
│   ├── StartupService.cs
│   ├── SingleInstanceService.cs
│   ├── AttachmentClient.cs
│   ├── FileCacheService.cs
│   ├── ClipboardService.cs
│   ├── UpdateClient.cs
│   ├── UpdateInstallService.cs
│   └── AppSettingsService.cs
│
├── Data/
│   ├── LocalDbContext.cs
│   ├── LocalEntities/
│   └── LocalMigrations/
│
├── Models/
├── Controls/
├── Converters/
├── Resources/
└── Program.cs
```

## 9.2 客户端 UI 布局

采用类似微信的双栏布局：

```text
┌────────────────────────────────────────────┐
│ 顶部：用户、设置、搜索、连接状态              │
├───────────────┬────────────────────────────┤
│ 会话列表       │ 当前聊天窗口                 │
│               │                            │
│ 频道           │ 消息列表                     │
│ 私聊           │                            │
│ 未读数         │ 输入框                       │
└───────────────┴────────────────────────────┘
```

## 9.3 客户端核心状态

客户端必须维护：

```text
当前用户
当前会话
会话列表
本地消息缓存
SignalR 连接状态
最后同步消息 ID
未读数量
通知记录
登录令牌
服务器地址
客户端版本
```

## 9.4 WPF 注意事项

必须注意：

- 消息列表要使用虚拟化；
- UI 集合更新必须回到 UI 线程；
- 文件上传下载不能阻塞 UI；
- 图片缩略图要异步加载；
- 大图片不能直接无限制加载原图；
- 窗口关闭默认隐藏到托盘；
- 真正退出必须走托盘菜单或设置中的退出按钮。

---

# 10. Shared 共享协议设计

## 10.1 枚举

```csharp
public enum ConversationType
{
    PublicChannel = 1,
    PrivateChannel = 2,
    Direct = 3
}

public enum MessageType
{
    Text = 1,
    Image = 2,
    File = 3,
    System = 4
}

public enum MessageSendStatus
{
    Sending = 1,
    Sent = 2,
    Failed = 3
}

public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    ServerUnavailable = 4
}
```

## 10.2 关键 DTO

### LoginRequest

```csharp
public sealed record LoginRequest(
    string UserName,
    string Password,
    string DeviceName,
    string ClientVersion);
```

### LoginResponse

```csharp
public sealed record LoginResponse(
    Guid UserId,
    string DisplayName,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string ServerVersion,
    string MinimumSupportedClientVersion);
```

### ConversationDto

```csharp
public sealed record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Name,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long LastMessageId,
    int UnreadCount);
```

### MessageDto

```csharp
public sealed record MessageDto(
    long Id,
    Guid ClientMessageId,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<AttachmentDto> Attachments,
    DateTimeOffset CreatedAt);
```

### SendMessageRequest

```csharp
public sealed record SendMessageRequest(
    Guid ClientMessageId,
    Guid ConversationId,
    MessageType Type,
    string? Content,
    long? ReplyToMessageId,
    IReadOnlyList<Guid> AttachmentIds,
    IReadOnlyList<Guid> MentionUserIds);
```

### AttachmentDto

```csharp
public sealed record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long Size,
    string DownloadUrl,
    string? ThumbnailUrl);
```

### SyncResponse

```csharp
public sealed record SyncResponse(
    IReadOnlyList<MessageDto> Messages,
    long LatestMessageId);
```

## 10.3 SignalR 事件

### 服务端推送给客户端

```text
NewMessage(MessageDto message)
ConversationUpdated(ConversationDto conversation)
UserPresenceChanged(UserPresenceDto presence)
ForceLogout(string reason)
ServerNotice(string message)
```

### 客户端连接后

客户端连接成功后必须：

```text
1. 认证
2. 加入自己有权限的会话组
3. 调用补拉接口
4. 更新连接状态
```

---

# 11. 数据库设计

## 11.1 服务端数据库表

### Users

```text
Id                         TEXT PRIMARY KEY
UserName                   TEXT NOT NULL UNIQUE
DisplayName                TEXT NOT NULL
AvatarAttachmentId          TEXT NULL
PasswordHash               TEXT NOT NULL
IsAdmin                    INTEGER NOT NULL
IsDisabled                 INTEGER NOT NULL
CreatedAt                  TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
LastLoginAt                TEXT NULL
LastOnlineAt               TEXT NULL
```

### RefreshTokens

```text
Id                         TEXT PRIMARY KEY
UserId                     TEXT NOT NULL
TokenHash                  TEXT NOT NULL
DeviceName                 TEXT NOT NULL
CreatedAt                  TEXT NOT NULL
ExpiresAt                  TEXT NOT NULL
RevokedAt                  TEXT NULL
```

### Conversations

```text
Id                         TEXT PRIMARY KEY
Type                       INTEGER NOT NULL
Name                       TEXT NOT NULL
AvatarAttachmentId          TEXT NULL
CreatedByUserId             TEXT NOT NULL
CreatedAt                  TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
IsDeleted                  INTEGER NOT NULL
```

### ConversationMembers

```text
ConversationId              TEXT NOT NULL
UserId                     TEXT NOT NULL
Role                       INTEGER NOT NULL
JoinedAt                   TEXT NOT NULL
LastReadMessageId           INTEGER NOT NULL DEFAULT 0
IsMuted                    INTEGER NOT NULL DEFAULT 0

PRIMARY KEY (ConversationId, UserId)
```

### Messages

```text
Id                         INTEGER PRIMARY KEY AUTOINCREMENT
ClientMessageId             TEXT NOT NULL
ConversationId              TEXT NOT NULL
SenderId                   TEXT NOT NULL
Type                       INTEGER NOT NULL
Content                    TEXT NULL
ReplyToMessageId            INTEGER NULL
CreatedAt                  TEXT NOT NULL
EditedAt                   TEXT NULL
DeletedAt                  TEXT NULL

UNIQUE(SenderId, ClientMessageId)
```

### MessageMentions

```text
MessageId                  INTEGER NOT NULL
MentionedUserId             TEXT NOT NULL

PRIMARY KEY (MessageId, MentionedUserId)
```

### Attachments

```text
Id                         TEXT PRIMARY KEY
MessageId                  INTEGER NULL
UploaderUserId              TEXT NOT NULL
OriginalFileName            TEXT NOT NULL
StoredFileName              TEXT NOT NULL
ContentType                TEXT NOT NULL
Size                       INTEGER NOT NULL
Sha256                     TEXT NULL
CreatedAt                  TEXT NOT NULL
```

### AppSettings

```text
Key                        TEXT PRIMARY KEY
Value                      TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
```

## 11.2 服务端索引

必须建立：

```text
Users.UserName
Conversations.Type
ConversationMembers.UserId
Messages.ConversationId, Messages.Id
Messages.SenderId, Messages.ClientMessageId
Messages.CreatedAt
Attachments.MessageId
Attachments.OriginalFileName
```

搜索第一版使用 `LIKE`，可以为常用字段加普通索引。

## 11.3 客户端本地数据库表

### LocalConversations

```text
Id                         TEXT PRIMARY KEY
Type                       INTEGER NOT NULL
Name                       TEXT NOT NULL
AvatarUrl                  TEXT NULL
LastMessageId               INTEGER NOT NULL DEFAULT 0
UnreadCount                INTEGER NOT NULL DEFAULT 0
LastOpenedAt               TEXT NULL
UpdatedAt                  TEXT NOT NULL
```

### LocalMessages

```text
Id                         INTEGER PRIMARY KEY
ClientMessageId             TEXT NOT NULL
ConversationId              TEXT NOT NULL
SenderId                   TEXT NOT NULL
SenderDisplayName           TEXT NOT NULL
Type                       INTEGER NOT NULL
Content                    TEXT NULL
ReplyToMessageId            INTEGER NULL
CreatedAt                  TEXT NOT NULL
IsRead                     INTEGER NOT NULL DEFAULT 0
IsNotified                 INTEGER NOT NULL DEFAULT 0
LocalSendStatus             INTEGER NOT NULL
```

### LocalAttachments

```text
Id                         TEXT PRIMARY KEY
MessageId                  INTEGER NULL
OriginalFileName            TEXT NOT NULL
ContentType                TEXT NOT NULL
Size                       INTEGER NOT NULL
DownloadUrl                TEXT NOT NULL
LocalPath                  TEXT NULL
ThumbnailLocalPath          TEXT NULL
DownloadStatus             INTEGER NOT NULL
```

### LocalAppState

```text
Key                        TEXT PRIMARY KEY
Value                      TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
```

必须保存：

```text
LastSyncedMessageId
LastNotifiedMessageId
CurrentUserId
ServerBaseUrl
ClientVersion
WindowBounds
Theme
```

---

# 12. 消息可靠性设计

## 12.1 消息发送流程

```text
用户输入消息
客户端生成 ClientMessageId
客户端插入本地 LocalMessages，状态 Sending
客户端 POST /api/messages
服务端验证权限
服务端检查 SenderId + ClientMessageId 是否已存在
如果已存在，直接返回已有 MessageDto
如果不存在，写入 Messages
服务端提交数据库事务
服务端返回 MessageDto
服务端 SignalR 推送 NewMessage
客户端收到响应后更新本地消息为 Sent
其他客户端收到 NewMessage 后入库和通知
```

## 12.2 幂等要求

服务端必须保证：

```text
同一 SenderId + ClientMessageId 只能生成一条服务端消息
```

这样客户端重试不会产生重复消息。

## 12.3 接收去重

客户端收到消息时：

```text
如果 LocalMessages 已存在 MessageId：
    忽略重复插入
    不重复通知
否则：
    插入本地数据库
    更新会话
    判断是否通知
```

## 12.4 断线补拉

客户端记录：

```text
LastSyncedMessageId
```

重连后：

```text
GET /api/sync?afterMessageId=LastSyncedMessageId
```

服务端返回当前用户有权限访问的所有新消息。

客户端按 MessageId 升序处理。

## 12.5 补拉去重

补拉消息必须走和实时推送相同的入库逻辑。

不要写两套插入逻辑。

推荐：

```text
ProcessIncomingMessage(MessageDto message, IncomingMessageSource source)
```

source 可取：

```text
Realtime
Sync
History
SendResponse
```

## 12.6 未读处理

如果消息满足：

```text
发送者不是当前用户
且 当前会话不是正在前台查看的会话
```

则：

```text
会话未读数 +1
总未读数 +1
显示新消息分割线
```

打开会话时：

```text
清除本地未读
调用 POST /api/conversations/{id}/read
```

第一版不需要对方已读回执。

---

# 13. 通知可靠性设计

## 13.1 通知触发条件

新消息满足以下条件时通知：

```text
发送者不是当前用户
且 消息未通知过
且 客户端未处于免打扰
且 (
    是私聊消息
    或者 是 @ 当前用户 的频道消息
    或者 是开启通知的普通频道消息
)
```

第一版普通频道默认开启通知。

## 13.2 不通知条件

以下情况不通知：

```text
消息由当前用户发送
当前正在查看对应会话且窗口在前台
消息已经通知过
用户彻底退出客户端
Windows 系统通知被用户关闭
```

## 13.3 通知动作

触发通知时执行：

```text
写入本地数据库 IsNotified = true
弹 Windows 通知
播放提示音
必要时任务栏闪烁
更新托盘未读数
```

## 13.4 Windows 通知

通知内容：

```text
标题：发送人 - 会话名
正文：消息摘要
参数：conversationId + messageId
```

点击通知后：

```text
打开主窗口
取消最小化
激活窗口
切换到对应会话
滚动到对应消息附近
```

## 13.5 任务栏闪烁

使用 Windows API：

```text
FlashWindowEx
```

触发条件：

```text
主窗口不在前台
且 收到需要提醒的新消息
```

停止闪烁条件：

```text
用户激活窗口
用户打开对应会话
```

## 13.6 托盘

必须实现：

```text
托盘图标
托盘总未读数
打开主窗口
彻底退出
连接状态提示
```

关闭窗口时：

```text
隐藏窗口
保留进程
保留 SignalR 连接
保留通知能力
```

## 13.7 单实例运行

必须实现：

```text
同一台电脑同一时间只能运行一个 RelayCove.Client 实例
```

再次启动时：

```text
通知已有实例显示主窗口
当前新进程退出
```

可使用：

```text
Mutex
Named Pipe
```

第一版可以只用 Mutex + 激活窗口。

---

# 14. 附件系统设计

## 14.1 上传流程

```text
用户选择文件
客户端检查大小
客户端 POST /api/attachments 上传文件
服务端验证用户身份
服务端检查大小限制
服务端生成 AttachmentId
服务端生成 StoredFileName
服务端保存文件到 uploads
服务端写入 Attachments 表
服务端返回 AttachmentDto
客户端发送消息时带 AttachmentIds
```

## 14.2 存储文件名

不得直接使用原始文件名作为物理文件名。

推荐：

```text
{AttachmentId}_{RandomSuffix}{Extension}
```

例如：

```text
8e276fd8-b341-4cb2-b1f0-7e1f6d0a21f2_a93f.png
```

## 14.3 下载流程

```text
客户端请求 GET /api/attachments/{id}/download
服务端验证用户身份
服务端验证用户是否有权限访问该附件所属会话
服务端返回文件流
客户端保存到本地缓存目录
客户端更新 LocalAttachments.LocalPath
```

## 14.4 图片缩略图

第一版可以采用：

```text
客户端本地生成缩略图
```

服务端不必第一版生成缩略图服务。

## 14.5 缓存目录

客户端缓存目录：

```text
%LOCALAPPDATA%/RelayCove/cache
```

日志目录：

```text
%LOCALAPPDATA%/RelayCove/logs
```

配置目录：

```text
%APPDATA%/RelayCove
```

---

# 15. 搜索系统设计

## 15.1 第一版搜索目标

第一版搜索必须简单可用：

- 中文关键词；
- 部分词匹配；
- 消息正文；
- 附件原始文件名；
- 当前会话搜索；
- 全局搜索；
- 结果点击跳转。

## 15.2 搜索权限

用户只能搜索自己有权限查看的会话。

服务端必须基于当前用户过滤：

```text
ConversationMembers
```

不要只在客户端过滤。

## 15.3 搜索接口

```text
GET /api/search?keyword=xxx&conversationId=optional&limit=50
```

返回：

```text
messageId
conversationId
conversationName
senderName
snippet
createdAt
matchedAttachmentFileName
```

## 15.4 查询方式

第一版可使用 SQLite LIKE：

```sql
WHERE Content LIKE '%' || @keyword || '%'
```

附件文件名：

```sql
WHERE OriginalFileName LIKE '%' || @keyword || '%'
```

## 15.5 跳转原消息

点击搜索结果：

```text
打开会话
如果本地没有该消息附近上下文，则调用服务端拉取 messageId 附近消息
滚动定位到该消息
短暂高亮
```

需要接口：

```text
GET /api/conversations/{conversationId}/messages/around/{messageId}?before=20&after=20
```

---

# 16. 自动更新设计

## 16.1 第一版更新目标

第一版自动更新只做完整安装包更新。

必须支持：

- 启动时检查更新；
- 设置页手动检查更新；
- 显示新版本；
- 显示更新说明；
- 下载安装包；
- 校验 SHA-256；
- 关闭客户端；
- 启动安装包；
- 安装后重新启动；
- 服务端最低版本强制限制。

## 16.2 更新清单

接口：

```text
GET /api/updates/manifest
```

返回：

```json
{
  "version": "1.0.1",
  "minimumSupportedVersion": "1.0.0",
  "downloadUrl": "https://chat.example.com/downloads/RelayCove-1.0.1.exe",
  "sha256": "安装包SHA256",
  "mandatory": false,
  "releaseNotes": "修复通知和附件上传问题"
}
```

## 16.3 客户端更新流程

```text
启动客户端
请求更新清单
比较当前版本和最新版本
如果当前版本 < minimumSupportedVersion：
    弹出强制更新窗口
    不允许继续使用
如果有普通更新：
    提示用户更新
用户确认
下载完整安装包
校验 SHA-256
启动 Updater
关闭主程序
Updater 启动安装包
```

## 16.4 不做内容

第一版不做：

- 增量更新；
- 差分补丁；
- 静默安装；
- 灰度发布；
- 自动回滚；
- 多更新通道；
- 复杂更新服务。

---

# 17. 管理员功能设计

## 17.1 管理入口

管理员功能直接放在 Windows 客户端中。

不单独开发 Web 后台。

入口：

```text
设置 -> 管理员
```

只有管理员可见。

## 17.2 第一版管理员功能

必须实现：

- 创建账号；
- 禁用账号；
- 删除账号；
- 重置密码；
- 创建公共频道；
- 创建私有频道；
- 修改频道；
- 删除频道；
- 管理私有频道成员；
- 查看服务器状态；
- 设置单个附件大小上限。

## 17.3 服务器状态

显示：

```text
服务端版本
运行时间
当前在线连接数
数据库文件大小
附件目录大小
最近一次错误摘要
```

## 17.4 管理权限

服务端必须校验：

```text
IsAdmin == true
```

不要只在客户端隐藏按钮。

---

# 18. 日志与故障排查

## 18.1 客户端日志

必须记录：

- 启动；
- 登录；
- 自动登录；
- SignalR 连接；
- SignalR 断开；
- SignalR 重连；
- 补拉开始；
- 补拉结果；
- 消息发送；
- 消息发送失败；
- 消息去重；
- 通知触发；
- 通知失败；
- 任务栏闪烁；
- 附件上传；
- 附件下载；
- 自动更新检查；
- 自动更新失败；
- 未处理异常。

## 18.2 服务端日志

必须记录：

- 服务启动；
- 登录成功和失败；
- Token 刷新；
- 消息写入；
- 消息推送；
- 附件上传；
- 附件下载；
- 搜索请求；
- 权限拒绝；
- 数据库异常；
- 未处理异常；
- 管理员操作。

## 18.3 日志保留

第一版：

```text
按天滚动
保留最近 14 天
```

## 18.4 日志注意事项

不得记录：

- 明文密码；
- Token 完整值；
- 敏感密钥；
- 附件完整内容。

---

# 19. VPS 部署方案

## 19.1 目录结构

```text
/opt/relaycove/
├── app/
│   └── RelayCove.Server
├── data/
│   ├── relaycove.db
│   └── uploads/
├── logs/
└── updates/
    ├── manifest.json
    └── RelayCove-1.0.1.exe
```

## 19.2 systemd 服务

示例：

```ini
[Unit]
Description=RelayCove Server
After=network.target

[Service]
WorkingDirectory=/opt/relaycove/app
ExecStart=/opt/relaycove/app/RelayCove.Server
Restart=always
RestartSec=5
User=relaycove
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

## 19.3 Nginx 配置要点

必须支持：

- HTTPS；
- WebSocket；
- 大文件上传；
- 反向代理到 ASP.NET Core。

示意：

```nginx
location / {
    proxy_pass http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_read_timeout 3600;
    client_max_body_size 200m;
}
```

## 19.4 服务端配置

`appsettings.Production.json` 示例：

```json
{
  "RelayCove": {
    "Storage": {
      "UploadRoot": "/opt/relaycove/data/uploads"
    },
    "Upload": {
      "MaxFileSizeMb": 200
    },
    "Update": {
      "ManifestPath": "/opt/relaycove/updates/manifest.json"
    }
  },
  "ConnectionStrings": {
    "Default": "Data Source=/opt/relaycove/data/relaycove.db"
  }
}
```

---

# 20. 开发阶段拆分

AI Agent 必须按以下顺序执行。

---

## 阶段 0：初始化仓库

目标：

```text
创建解决方案、项目结构、README、基础配置。
```

任务：

1. 创建 `RelayCove.sln`；
2. 创建 Client、Server、Shared、Updater 项目；
3. 开启 Nullable；
4. 配置基础日志；
5. 配置 `.gitignore`；
6. 创建 README；
7. 创建 docs 目录；
8. 确保可以编译。

验收：

```text
dotnet build
```

必须通过。

---

## 阶段 1：共享协议和基础模型

目标：

```text
定义 DTO、枚举、错误码、版本信息。
```

任务：

1. 定义 ConversationType；
2. 定义 MessageType；
3. 定义 MessageDto；
4. 定义 ConversationDto；
5. 定义 AttachmentDto；
6. 定义 LoginRequest/LoginResponse；
7. 定义 SendMessageRequest；
8. 定义 SyncResponse；
9. 定义 ApiResult 或统一错误响应。

验收：

```text
Shared 项目可以被 Client 和 Server 引用
dotnet build 通过
```

---

## 阶段 2：服务端数据库和认证

目标：

```text
实现账号登录和管理员预创建账号。
```

任务：

1. 创建 EF Core DbContext；
2. 创建 Users 表；
3. 创建 RefreshTokens 表；
4. 实现密码哈希；
5. 实现登录接口；
6. 实现刷新 Token；
7. 实现当前用户接口；
8. 启动时支持创建默认管理员；
9. 实现管理员创建用户。

验收：

```text
可以创建管理员
可以登录
可以拿到 accessToken
可以调用 /api/auth/me
禁用用户无法登录
```

---

## 阶段 3：会话和成员

目标：

```text
实现公共频道、私有频道、私聊。
```

任务：

1. 创建 Conversations 表；
2. 创建 ConversationMembers 表；
3. 管理员创建公共频道；
4. 管理员创建私有频道；
5. 管理员管理私有频道成员；
6. 获取当前用户会话列表；
7. 创建或获取一对一私聊；
8. 权限校验。

验收：

```text
公共频道所有人可见
私有频道只有成员可见
私聊会话只对两人可见
无权限用户不能访问会话
```

---

## 阶段 4：消息入库和历史消息

目标：

```text
实现可靠消息保存。
```

任务：

1. 创建 Messages 表；
2. 创建 MessageMentions 表；
3. 实现 POST /api/messages；
4. 实现幂等：SenderId + ClientMessageId 唯一；
5. 实现历史消息接口；
6. 实现消息 around 查询；
7. 实现 read 接口。

验收：

```text
能发送文字消息
重复提交同一 ClientMessageId 不会生成重复消息
能拉历史消息
无权限用户不能发送或读取消息
```

---

## 阶段 5：SignalR 实时推送

目标：

```text
实现实时消息到达。
```

任务：

1. 创建 ChatHub；
2. 客户端连接时认证；
3. 服务端把用户加入有权限的会话组；
4. 新消息入库后推送 NewMessage；
5. 客户端接收 NewMessage；
6. 客户端显示连接状态。

验收：

```text
A 发消息
B 在线时立即收到
B 无权限会话不会收到
服务端日志记录推送行为
```

---

## 阶段 6：客户端本地缓存和消息同步

目标：

```text
实现客户端本地数据库、断线补拉和去重。
```

任务：

1. 创建客户端 SQLite；
2. 创建 LocalConversations；
3. 创建 LocalMessages；
4. 创建 LocalAttachments；
5. 保存 LastSyncedMessageId；
6. 实现 ProcessIncomingMessage；
7. 实现消息去重；
8. 实现启动后同步；
9. 实现重连后同步；
10. 实现未读数。

验收：

```text
断网期间的消息恢复网络后可补拉
消息不重复显示
未读数量正确
客户端重启后仍能显示最近消息
```

---

## 阶段 7：Windows 通知、托盘、闪烁

目标：

```text
实现第一核心：通知可靠。
```

任务：

1. 实现 Windows 系统通知；
2. 实现通知点击跳转；
3. 实现提示音；
4. 实现 FlashWindowEx；
5. 实现托盘图标；
6. 实现托盘未读数；
7. 实现关闭窗口进入托盘；
8. 实现彻底退出；
9. 实现开机启动；
10. 实现单实例运行。

验收：

```text
B 最小化时收到通知
B 关闭窗口进入托盘后仍收到通知
收到消息时任务栏闪烁
点击通知打开对应会话
再次启动客户端不会出现第二个实例
```

---

## 阶段 8：聊天 UI

目标：

```text
实现可日常使用的界面。
```

任务：

1. 登录界面；
2. 主窗口；
3. 会话列表；
4. 聊天消息列表；
5. 消息输入框；
6. Enter 发送；
7. Ctrl+Enter 换行；
8. 回复消息；
9. @用户；
10. 链接识别；
11. 复制消息；
12. 日期分割线；
13. 新消息分割线；
14. 一键回到最新消息；
15. 有新消息提示；
16. 发送失败重试。

验收：

```text
普通用户可以完成日常聊天
消息列表滚动体验正常
查看历史消息时不会被新消息强制拉到底
```

---

## 阶段 9：附件

目标：

```text
实现图片、文件、截图、拖拽上传。
```

任务：

1. 服务端附件上传接口；
2. 服务端附件下载接口；
3. 附件权限校验；
4. 客户端选择文件；
5. 客户端拖拽文件；
6. 客户端粘贴截图；
7. 上传进度；
8. 下载进度；
9. 图片缩略图；
10. 图片查看原图；
11. 打开文件；
12. 打开文件所在目录；
13. 上传失败重试。

验收：

```text
可以发图片
可以发文件
可以发视频文件
可以粘贴截图
可以拖拽上传
无权限用户不能下载附件
```

---

## 阶段 10：搜索

目标：

```text
实现中文部分匹配和附件文件名搜索。
```

任务：

1. 服务端搜索接口；
2. 权限过滤；
3. 搜索消息正文；
4. 搜索附件原始文件名；
5. 客户端搜索 UI；
6. 当前会话搜索；
7. 全局搜索；
8. 结果高亮；
9. 点击跳转原消息。

验收：

```text
中文关键词可搜索
中文部分词可搜索
附件文件名可搜索
私有频道权限过滤正确
点击结果能定位原消息
```

---

## 阶段 11：管理员页面

目标：

```text
实现基础管理。
```

任务：

1. 管理员入口；
2. 创建账号；
3. 禁用账号；
4. 删除账号；
5. 重置密码；
6. 创建频道；
7. 修改频道；
8. 删除频道；
9. 管理私有频道成员；
10. 查看服务器状态；
11. 设置附件大小上限。

验收：

```text
普通用户看不到管理员入口
普通用户无法调用管理员接口
管理员可以完成基础维护
```

---

## 阶段 12：自动更新

目标：

```text
实现简化自动更新。
```

任务：

1. 服务端提供更新清单；
2. 客户端启动检查；
3. 设置页手动检查；
4. 新版本提示；
5. 下载完整安装包；
6. SHA-256 校验；
7. 调用 Updater；
8. 关闭主程序；
9. 启动安装包；
10. 最低版本强制更新。

验收：

```text
发现新版本
能下载安装包
校验失败会阻止安装
强制更新时不能继续使用旧版
普通更新可稍后处理
```

---

## 阶段 13：部署脚本和发布

目标：

```text
让项目可以部署到 VPS，客户端可以打包。
```

任务：

1. 服务端发布脚本；
2. systemd 文件；
3. Nginx 配置示例；
4. 客户端发布脚本；
5. 安装包制作说明；
6. 更新清单生成脚本；
7. 部署文档。

验收：

```text
新 VPS 可按文档部署
Windows 客户端可安装运行
更新清单可生成
服务重启后客户端可恢复连接
```

---

# 21. 每阶段验收标准

## 21.1 通知验收

必须手动测试：

```text
A 在线，B 在线
A 给 B 发私聊
B 前台收到

B 最小化
A 给 B 发私聊
B 弹 Windows 通知
B 任务栏闪烁

B 关闭窗口进入托盘
A 给 B 发私聊
B 仍弹通知

B 断网
A 给 B 发 3 条消息
B 恢复网络
B 自动补拉 3 条
B 不重复显示
B 不重复通知

B 彻底退出
A 给 B 发消息
B 不要求实时通知
B 下次启动后能补拉未读
```

## 21.2 消息验收

```text
发送文字成功
发送多行成功
发送失败可重试
重复提交不会重复生成消息
历史消息按时间正确
回复消息可显示被回复内容
@用户可解析
链接可点击
```

## 21.3 附件验收

```text
图片上传成功
文件上传成功
视频作为文件上传成功
粘贴截图成功
拖拽上传成功
上传进度显示
下载进度显示
下载后可打开
下载后可打开所在目录
无权限用户无法下载
```

## 21.4 搜索验收

```text
中文完整词可搜索
中文部分词可搜索
附件文件名可搜索
当前会话搜索正确
全局搜索正确
私有频道权限正确
点击搜索结果可跳转
```

## 21.5 更新验收

```text
客户端能检查更新
有新版本时显示更新说明
能下载安装包
SHA-256 不匹配时拒绝安装
最低版本不足时强制更新
更新失败不破坏当前可运行版本
```

---

# 22. 禁止过度设计清单

AI Agent 必须避免以下行为：

## 22.1 禁止架构过度

不要引入：

```text
微服务
服务注册发现
API Gateway
消息队列
事件总线
CQRS
Event Sourcing
复杂 DDD
多数据库
Redis 缓存
分布式锁
Elasticsearch
对象存储
Kubernetes
```

## 22.2 禁止功能膨胀

不要第一版加入：

```text
语音通话
视频通话
Web 端
Android 端
插件系统
机器人市场
端到端加密
企业组织架构
复杂权限矩阵
OAuth 第三方登录
SSO
```

## 22.3 禁止 UI 过度

不要第一版追求：

```text
复杂动画
复杂主题系统
皮肤市场
高度自定义布局
花哨动效
```

UI 目标是：

```text
清楚
稳定
像正常聊天工具
```

---

# 23. AI Agent 任务模板

后续每次让 AI Agent 开发模块时，可以使用以下模板。

```markdown
# 任务：实现 RelayCove 的【模块名】

## 背景

RelayCove 是一个 WPF + ASP.NET Core + SignalR + SQLite 的小团队私域聊天工具。

当前阶段目标是：【填写阶段目标】

## 必须实现

1. ...
2. ...
3. ...

## 不允许实现

- 不要引入 Redis
- 不要引入 MQ
- 不要引入 Elasticsearch
- 不要重构无关模块
- 不要加入未要求功能

## 技术要求

- C# Nullable 开启
- async/await
- 日志完整
- 异常处理完整
- UI 不阻塞
- 服务端接口必须校验权限

## 验收标准

1. dotnet build 通过
2. ...
3. ...

## 输出要求

完成后请说明：

1. 修改了哪些文件
2. 新增了哪些文件
3. 如何运行
4. 如何验证
5. 已知限制
6. 下一步建议
```

---

# 24. 最终交付标准

RelayCove 第一版完成后，必须满足：

## 24.1 可运行

- 服务端可部署到 VPS；
- 客户端可安装到 Windows；
- 登录可用；
- 聊天可用；
- 附件可用；
- 搜索可用；
- 管理可用；
- 更新可用。

## 24.2 可靠性

- 消息先入库再推送；
- 消息发送具备幂等性；
- SignalR 断线能持续重连；
- 重连后能补拉漏消息；
- 消息不重复显示；
- 通知不重复弹出；
- 客户端关闭窗口后仍在托盘运行；
- 托盘状态清楚；
- 任务栏闪烁可用；
- 点击通知可跳转。

## 24.3 可维护性

- 代码结构清楚；
- 模块边界明确；
- 日志可用于排查问题；
- 数据库迁移可运行；
- 配置不硬编码；
- README 能指导运行；
- docs 能指导部署和开发。

## 24.4 开源质量

GitHub 仓库至少包含：

```text
README.md
LICENSE
docs/architecture.md
docs/deployment.md
docs/api.md
docs/database.md
docs/update.md
简单截图或界面说明
开发路线图
```

README 建议包含：

```markdown
# RelayCove

A lightweight self-hosted chat application for small teams.

## Features

- Self-hosted
- Windows desktop client
- Channels and direct messages
- File and image attachments
- Reliable desktop notifications
- Reconnect and message sync
- Searchable message history
- Simple admin panel
- Lightweight SQLite backend

## Tech Stack

- WPF
- ASP.NET Core
- SignalR
- SQLite

## Status

Early development.
```

---

# 25. 最重要的实现顺序

必须先做通知闭环，再做体验优化。

推荐最小闭环：

```text
登录
一个频道
文字消息入库
SignalR 推送
客户端本地缓存
Windows 通知
任务栏闪烁
托盘常驻
断线重连
断线补拉
消息去重
通知去重
```

只有这个闭环稳定后，再继续做：

```text
完整聊天 UI
附件
搜索
管理员
自动更新
```

最终原则：

```text
先做一个界面普通但绝不漏消息的版本，
再做一个体验舒服的聊天工具。
```
