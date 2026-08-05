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
服务端在 SQLite 中 INSERT-first
服务端提交数据库事务
服务端返回 MessageDto（新插入 201，幂等重放 200）
仅新插入请求在提交后尝试一次 SignalR NewMessage
客户端本地入库
客户端刷新 UI
```

### 实时推送

SignalR 只负责实时通知和事件推送。

SignalR 不作为唯一可靠消息来源。

### 断线恢复

客户端必须记录服务端解释的 `LastSyncCursor`，不得用最后一条可见消息的 ID 推算游标。

重连成功后调用：

```text
GET /api/sync?cursor={long}&snapshotUpperBound={long?}&limit={int?}
```

首页由服务端在同一只读事务中捕获固定消息 ID 上界；后续页原样携带该上界。每页提交本地事务后才推进游标，循环到 `HasMore=false` 才完成一轮补拉。SignalR 只缩短可见延迟，周期同步负责补偿推送失败。

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

阶段 3 冻结 `POST /api/conversations` 为判别请求：Public/Private 传 `Type + Name`，只允许数据库当前全局管理员创建，且创建者成为会话内 Administrator；Direct 传 `Type=Direct + ParticipantUserId`，任意正常认证用户可创建或获取，canonical pair 新建返回 201、已存在或恢复返回 200。私有成员 POST 是 201/200 幂等 upsert，DELETE 不存在成员仍为 204；写事务内必须复核全局管理员或当前会话 Administrator。全局管理员的成员管理覆盖不自动授予私有内容读取权。

`GET /api/conversations` 必须在单个权威查询中返回非分页完整集合和 `Complete=true`；Public 对全部正常认证用户隐式可见，Private/Direct 仅当前成员可见，Direct 名称按另一参与者昵称派生。未知、删除或不可访问会话统一返回 `403 ConversationAccessRevoked`；错误会话类型的成员操作返回 `409 ConversationTypeConflict`。Public 不提供伪造成员清单，Direct 成员不可变。

### 消息接口

```text
GET  /api/conversations/{conversationId}/messages?beforeMessageId=xxx&limit=50
POST /api/messages
GET  /api/sync?cursor={long}&snapshotUpperBound={long?}&limit={int?}
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
GET    /api/admin/users
POST   /api/admin/users
PUT    /api/admin/users/{id}
DELETE /api/admin/users/{id}
POST   /api/admin/users/{id}/reset-password
GET    /api/admin/channels
GET    /api/admin/status
GET    /api/admin/settings/upload
PUT    /api/admin/settings/upload
```

`POST /api/admin/users` 的 v1 请求包含 `UserName`、`DisplayName`、`Password`、`IsAdmin`；成功返回不含密码与哈希的用户响应。未认证为 401、数据库当前非管理员为 403、结构或密码策略失败为 400、规范化用户名重复为稳定 409。同名并发创建必须只有一个成功，管理员审计日志不得包含请求对象、用户名、昵称、密码或哈希。

阶段 11 冻结删除用户为不可恢复的逻辑退役：设置 `RetiredAt`、禁用账号、递增 token 代际并撤销全部 refresh token，同时保留用户名和历史外键；用户名不可复用。禁用、恢复、重置密码和退役都使旧 access/refresh token 失效，禁止自禁用、自退役和移除最后一个正常管理员。频道改名/删除继续使用会话 API，Direct 不可修改，频道删除为软删除并在提交后尽力推送撤权。

`AppSettings` 的 `Uploads.MaximumFileBytes` 是无行时配置默认值之上的持久覆盖，取值固定为 1–100 MiB；每个上传在读取正文前只读取一次 effective 值。状态接口只暴露版本、启动/运行时间、连接数、数据库/附件总字节、effective 上限与脱敏错误类别/时间，不暴露路径、连接串、异常正文或身份数据。

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
AccountScopeId
当前会话
会话列表
本地消息缓存
SignalR 连接状态
LastSyncCursor
LastReadMessageId / PendingReadThroughMessageId
未读数量
IsNotificationHandled 通知状态
revoked deny-set / tombstone
登录令牌
服务器地址
客户端版本
```

本地数据库、文件缓存、同步游标、通知 Group 和撤权状态必须全部按 `AccountScopeId` 隔离；服务器或账户切换时取消旧作用域工作，绝不复用其消息、未读、游标、缓存或通知状态。

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

public enum ConversationMemberRole
{
    Member = 1,
    Administrator = 2
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

public enum IncomingMessageSource
{
    Realtime = 1,
    Sync = 2,
    History = 3,
    SendResponse = 4
}

public enum SyncReason
{
    Startup = 1,
    Reconnect = 2,
    WindowActivated = 3,
    Periodic = 4
}

public enum NotificationPolicy
{
    None = 0,
    PerMessage = 1,
    Summary = 2
}

public enum IncomingMessageMergeResult
{
    Inserted = 1,
    PendingPromoted = 2,
    Duplicate = 3,
    Conflict = 4
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

`LoginRequest` 与 `LoginResponse` 的实现必须覆盖 record 默认 `ToString()`：密码、Access Token 和 Refresh Token 只能输出 `[REDACTED]`，不得因对象插值或结构化日志意外泄露。传输 JSON 仍包含完成认证所需字段，服务端和客户端统一使用 Web camelCase 形状。

### ApiErrorResponse

```csharp
public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string? TraceId = null,
    IReadOnlyDictionary<string, string[]>? Details = null);
```

- `Code` 是客户端分支的稳定字符串；`Message` 只用于人类诊断或显示，不是兼容性键。`Details` 的键使用 Web camelCase 请求字段名，每个字段可包含多个错误。
- 第一批稳定错误码为 `ValidationFailed`、`AuthenticationFailed`、`AuthenticationRequired`、`AccessDenied`、`RateLimitExceeded`、`ServiceUnavailable`、`InternalServerError`、`UserNameAlreadyExists`、`UserNotFound`、`ConversationTypeConflict`、`MessageTypeUnsupported`、`SyncCursorInvalid`、`IdempotencyKeyReuse`、`ConversationAccessRevoked`。
- 未知用户名、密码错误和账号禁用统一返回 `401 AuthenticationFailed`，响应不得揭示账号是否存在或禁用；精确原因只进入不含机密的服务端诊断。
- 缺少或无效认证返回 `401 AuthenticationRequired`；身份有效但普通权限不足返回 `403 AccessDenied`；已撤销会话权限继续使用 `403 ConversationAccessRevoked`。
- 请求校验失败返回 `400 ValidationFailed`；同步游标和幂等键冲突使用各自已冻结的 `409` 错误码。错误响应、日志和 TraceId 都不得包含密码、完整 Token 或密钥。

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
    long LastReadMessageId,
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
    IReadOnlyList<Guid> MentionUserIds,
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

阶段 4 首个发送切片只开放 `MessageType.Text`；Image/File 待附件存储上线，System 只由未来受控服务生成，其他类型请求返回 `409 MessageTypeUnsupported`。Text 精确保留，要求 1–4000 个有效 Unicode scalar value且非全空白，允许 TAB/CR/LF、拒绝其他控制字符，不 trim 或规范化。MentionUserIds 是最多 20 个当前可访问正常用户的无序唯一集合；ReplyToMessageId 必须属于同一会话；附件表上线前 AttachmentIds 必须为空。

`POST /api/messages` 必须先在同一 SQLite 写事务复核权限和引用，再 INSERT-first；只有目标 `(SenderId, ClientMessageId)` 唯一冲突可进入精确载荷/集合回读。新建 201、相同重放 200、不同载荷 409，且 INSERT 成功前不得更新 Conversation 或留下其他持久副作用。

History 响应使用 `MessageHistoryResponse(Messages, NextBeforeMessageId, HasMore)`；`beforeMessageId` 为排除边界，`limit` 默认 50、范围 1–100。查询按 ID 降序取 `limit+1`，响应按 ID 升序；有更多时下一 before 为本页最旧 ID，否则为 null。

read-through 契约使用 `MarkConversationReadRequest(long MessageId)` 与 `ConversationReadReceipt(Guid ConversationId, long LastReadMessageId)`。`POST /api/conversations/{conversationId}/read` 必须在 Serializable 写事务内先复核当前内容权限，再确认目标消息真实属于该会话，最后保存并返回 `MAX(old, MessageId)`；不存在/跨会话/非正目标稳定 400，不得接受任意极大 ID。Private/Direct 只更新当前成员；Public 正常用户首次成功上报时创建不对成员 API 暴露的个人状态行，Public 的成员管理/list 仍保持类型冲突。

around 契约使用 `MessageAroundResponse(Messages, TargetMessageId, HasMoreBefore, HasMoreAfter)`。`GET /api/conversations/{conversationId}/messages/around/{messageId}?before=20&after=20` 的 `before`/`after` 各允许 `0..100`；返回真实目标、最近的前后消息并按 ID 严格升序，双侧标志表示对应窗口外是否仍有消息。服务端必须先复核当前内容权限，再确认目标属于该会话，并在最终有限投影中再次绑定权限；不可访问会话稳定 403，可访问会话内不存在或跨会话目标稳定 400。

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
    long NextCursor,
    long SnapshotUpperBound,
    bool HasMore);
```

## 10.3 SignalR 事件

### 服务端推送给客户端

```text
NewMessage(MessageDto message)
ConversationUpdated(ConversationDto conversation)
ConversationAccessRevoked(Guid conversationId)
UserPresenceChanged(UserPresenceDto presence)
ForceLogout(string reason)
ServerNotice(string message)
```

### 客户端连接后

客户端连接成功后必须：

```text
1. 认证
2. 获取 Complete=true 的权威会话全集并完成本地对账
3. 按当前权威权限加入会话组
4. 调用补拉接口
5. 更新连接状态
```

SignalR 连接重建不会保留组成员关系。每次重连都必须重新执行权威对账并按当前权限加组，旧连接的组状态不得用作授权依据。

第一版服务端 Hub 固定为 `/hubs/chat` 且只允许认证连接，不暴露客户端自行加组的业务方法。JWT 的 `sub` 标准 GUID 是 SignalR 唯一用户标识；浏览器 WebSocket/SSE 所需的 `access_token` 查询参数只允许在该 Hub 路径提取，生产环境必须使用 HTTPS，并禁止默认 Information 请求日志记录完整查询字符串。连接在 access token 到期时关闭，由客户端以新 token 建立新连接。

每个新连接都从数据库重新读取当前可见会话并加入对应连接组；组只用于路由优化，不是授权真源。连接还加入 `(UserId, AccessTokenVersion)` 账户代际组。`NewMessage` 每次发布前另以一个数据库查询计算当前正常用户及其当前 token 代际：Public 为全部正常用户，Private/Direct 为当前成员；只向当前代际组投递，因此密码重置、禁用/恢复或退役前建立的旧连接不能继续收到新消息。发送者当前代际的其他设备仍收到回声；撤权提交前已经形成的在途帧仍由客户端 deny-set 防御。

客户端实时入口使用与服务端一致的 `Microsoft.AspNetCore.SignalR.Client 10.0.10`。连接 URI 只能由无 user-info、query、fragment 的绝对 HTTP(S) 服务端基址组合固定相对路径 `hubs/chat`；`AccessTokenProvider` 每次从当前认证会话读取最新 access token，不把 token 固化在连接对象、状态或日志中。客户端显式启动时报告 Connecting→Connected；初始连接失败报告 ServerUnavailable 并把异常返回调用者。已建立连接使用 SignalR 默认 0/2/10/30 秒自动重连并报告 Reconnecting→Connected，默认次数耗尽或非主动 Closed 报告 ServerUnavailable，主动 Stop/Dispose 报告 Disconnected；后续账户/同步 orchestrator 可显式再次启动，不在连接层隐藏无限重试。

`NewMessage`、`ConversationAccessRevoked` 和连接状态进入同一个进程内串行 sink。连接回调只按接收顺序排队，单消费者执行下一层处理；撤权回调完成前不得处理随后到达的消息。sink 异常必须记录不含 token、正文、显示名或用户名的元数据并允许队列继续，安全性的 fail-closed deny-set/tombstone 由下一层在处理撤权时先更新。连接层不直接访问 WPF Dispatcher、本地数据库、通知或 UI，具体适配器自行切回 UI 线程。

---

# 11. 数据库设计

## 11.1 服务端数据库表

### Users

```text
Id                         TEXT PRIMARY KEY
UserName                   TEXT NOT NULL UNIQUE
NormalizedUserName         TEXT NOT NULL UNIQUE
DisplayName                TEXT NOT NULL
AvatarAttachmentId          TEXT NULL
PasswordHash               TEXT NOT NULL
IsAdmin                    INTEGER NOT NULL
IsDisabled                 INTEGER NOT NULL
CreatedAt                  TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
LastLoginAt                TEXT NULL
LastOnlineAt               TEXT NULL
RetiredAt                  TEXT NULL
AccessTokenVersion         INTEGER NOT NULL DEFAULT 0
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

v1 登录名限制为 3–64 个 ASCII 字母、数字、点、下划线或连字符；`UserName` 保留原始大小写，所有写入通过同一实体方法同步生成 invariant-uppercase `NormalizedUserName`，登录查找和唯一约束只使用后者。Unicode 姓名使用 `DisplayName`，不把 ICU/NLS 相关大小写规则带入身份键。

服务端 GUID 以小写标准 `D` 文本保存；时间以固定 `yyyy-MM-ddTHH:mm:ss.fffZ` UTC 文本保存并拒绝非 UTC 写入。refresh token 原始值由 32 字节 CSPRNG 产生，表中只保存 `Base64Url(SHA-256(raw bytes))` 的 43 字符哈希；不得保存明文 token。密码使用 ASP.NET Core 版本化 `PasswordHasher` 格式，不自定义低层 PBKDF2 存储协议。

认证 token 细节遵循 `DEC-006`：access JWT 固定 `typ=at+jwt`、HS256、issuer/audience 与 `sub/jti/iat/exp`，并携带单调 `atv` 账户 token 代际；旧 token 缺失该 claim 时只兼容为代际 0。JWT 有效期 15 分钟并仅接受 30 秒时钟偏差；签名 key 至少 32 个随机字节且不得进入仓库。refresh token 有效期 30 天，每次使用都在 SQLite 非 deferred 写事务内条件撤销旧 token 并原子插入新 token；并发只有一个请求成功。JWT 验证后仍查库确认用户存在、未禁用、未退役且代际相等，logout 对任意 token 输入幂等返回 204。

管理员引导与创建账号遵循 `DEC-007`：bootstrap 默认关闭且凭据只从外部配置注入，运维先显式迁移数据库；启动服务只在整个 Users 表为空时创建首个管理员，已有任意用户时不得覆盖、提权或改密。新密码按 Unicode scalar value 要求 15–128 个字符，允许空格/Unicode、不加字符组合规则，并拒绝控制字符、常见弱密码和直接上下文派生。管理员权限不写入 access token；每次管理请求从数据库动态校验，并在创建用户写事务中再次确认 actor。

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
DirectParticipantKey        TEXT NULL
```

频道 `Name` 为 1–100 个 Unicode scalar value；Direct 的 `Name` 固定保存空字符串，响应展示名按当前用户从另一参与者动态生成。`DirectParticipantKey` 仅 Direct 必填，由两个不同参与者的小写标准 `D` GUID 按 ordinal 排序后以 `:` 连接，并在包含软删除行的全表范围永久唯一；重新发起已软删除的一对一会话时恢复原会话，不创建第二条历史线程。

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

`Role` 固定为 `Member=1`、`Administrator=2`；它是会话内角色，与 `Users.IsAdmin` 的全局服务管理员权限无关。`LastReadMessageId` 必须非负且只允许单调推进。会话正常删除只设置 `IsDeleted`；创建者用户外键限制硬删除，成员行在会话或成员用户被硬删除时级联清理。Direct 恰好两名 Member、创建者属于参与者，以及加入/重新加入时按当前消息最大 ID 初始化已读边界，由阶段 3 服务事务保证。

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

UNIQUE(SenderId, ClientMessageId)
```

第一版消息一经写入不可编辑、撤回或删除。所有 GUID 在 SQLite 中使用 `Guid.ToString("D").ToLowerInvariant()` 的规范文本；未来若支持消息变更，必须新增独立变更流，不能复用只向前扫描的新消息游标。

`Id` 必须实际生成 `INTEGER PRIMARY KEY AUTOINCREMENT`，保证已提交消息 ID 在数据库生命周期内不复用；空洞合法。Conversation 硬删级联 Messages，Sender 外键 Restrict，Reply 外键使用 NO ACTION：单独删除被回复消息仍失败，而同一条 Conversation 硬删语句可在语句末完成整组级联。Text 的数据库 CHECK 固定 Type/Content 对应、1–4000 长度和非空；完整 Unicode scalar、空白和控制字符语义由唯一写入实体/服务防守。

### MessageMentions

```text
MessageId                  INTEGER NOT NULL
MentionedUserId             TEXT NOT NULL

PRIMARY KEY (MessageId, MentionedUserId)
```

MessageMention 随 Message 硬删级联，MentionedUser 外键 Restrict，避免用户硬删改变不可变消息载荷。

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
Users.NormalizedUserName
RefreshTokens.TokenHash
Conversations.Type
Conversations.DirectParticipantKey (UNIQUE)
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
LastReadMessageId           INTEGER NOT NULL DEFAULT 0
PendingReadThroughMessageId INTEGER NULL
UnreadCount                INTEGER NOT NULL DEFAULT 0
IsMuted                    INTEGER NOT NULL DEFAULT 0
LastOpenedAt               TEXT NULL
UpdatedAt                  TEXT NOT NULL
```

### LocalMessages

```text
LocalId                    INTEGER PRIMARY KEY AUTOINCREMENT
ServerMessageId            INTEGER NULL UNIQUE
ClientMessageId             TEXT NOT NULL
ConversationId              TEXT NOT NULL
SenderId                   TEXT NOT NULL
SenderDisplayName           TEXT NOT NULL
Type                       INTEGER NOT NULL
Content                    TEXT NULL
ReplyToMessageId            INTEGER NULL
CreatedAt                  TEXT NOT NULL
IsRead                     INTEGER NOT NULL DEFAULT 0
IsNotificationHandled      INTEGER NOT NULL DEFAULT 0
LocalSendStatus             INTEGER NOT NULL

UNIQUE(SenderId, ClientMessageId)
```

pending 行使用 `LocalId` 定位、`ServerMessageId=NULL`；服务端 `MessageDto.Id` 只写入 `ServerMessageId`。这样发送响应、Realtime 回声与后续 Sync 可以提升同一行，不需要为未确认消息伪造服务端 ID。

### LocalAttachments

```text
Id                         TEXT PRIMARY KEY
LocalMessageId             INTEGER NULL
OriginalFileName            TEXT NOT NULL
ContentType                TEXT NOT NULL
Size                       INTEGER NOT NULL
DownloadUrl                TEXT NOT NULL
LocalPath                  TEXT NULL
ThumbnailLocalPath          TEXT NULL
DownloadStatus             INTEGER NOT NULL
```

`LocalAttachments.LocalMessageId` 引用 `LocalMessages.LocalId`，所以 pending 与已确认消息共用同一附件关系。提及用户 ID 以 `(LocalMessageId, MentionedUserId)` 唯一保存，供不可变载荷校验和本地展示使用。

### LocalAppState

```text
Key                        TEXT PRIMARY KEY
Value                      TEXT NOT NULL
UpdatedAt                  TEXT NOT NULL
```

必须保存：

```text
LastSyncCursor
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
客户端插入 LocalMessages pending 行（ServerMessageId=NULL，状态 Sending）
客户端 POST /api/messages
服务端在权威事务内验证权限并尝试 INSERT
新插入提交后返回 201；幂等重放回读并返回 200
服务端提交数据库事务
只有新插入请求在提交后尝试一次 SignalR NewMessage
客户端把 SendResponse 或 Realtime 回声合并到同一 pending 行并标记 Sent
```

## 12.2 幂等要求

- `POST /api/messages` 使用 INSERT-first，并由 `UNIQUE(SenderId, ClientMessageId)` 裁决并发。同一发送者和 `ClientMessageId` 只能生成一条消息。
- 服务端拒绝 `Guid.Empty`；非法 GUID 由请求绑定返回 `400`。GUID 一律以小写标准 `D` 格式写入 SQLite，防止 TEXT 唯一约束被不同文本形式绕过。
- 发送权限校验必须在幂等回读之前，并与消息写入处于同一权威事务边界；撤权后重放仍返回带稳定错误码 `ConversationAccessRevoked` 的 `403`，不能借幂等命中读回旧消息。
- 新插入在事务提交后返回 `201 Created`。只有赢得插入的请求允许尝试一次 `NewMessage` 推送；推送失败只记日志，不回滚、不改变 HTTP 结果，由周期同步补偿。
- 只捕获 `UNIQUE(SenderId, ClientMessageId)` 这一目标约束冲突。命中后在同一发送者范围回读原消息，返回 `200 OK`，不得再次推送；其他约束错误不得伪装成幂等重放。
- 重放的会话、类型、正文、回复目标、附件 ID 集合和提及用户 ID 集合必须与原请求语义相同。相同键携带不同有效载荷返回 `409 IdempotencyKeyReuse`。
- SignalR 可以向发送者连接广播以支持多设备；当前设备始终用 `(SenderId, ClientMessageId)` 把响应和回声合并到同一行。
- `NewMessage` 发布由 HTTP 发送端点只在命令返回 `Created` 后同步尝试一次；此时消息事务已经提交。`Replay`、校验/授权错误、幂等键冲突均不发布，并发同键只有赢得插入的请求发布。收件人查询或 SignalR 发送异常只记录消息 ID、会话 ID 和收件人数等非敏感元数据，不回滚消息、不改变 201，也不在请求内重试；固定上界 Sync 负责补偿。

## 12.3 本地消息身份与唯一合并路径

Realtime、Sync、History、SendResponse 全部调用同一个本地事务内合并函数。它分别查询 `serverHit`（`ServerMessageId`）与 `keyHit`（`SenderId + ClientMessageId`），并返回明确结果：

```text
serverHit 为空，keyHit 为空：Inserted
serverHit 为空，keyHit 命中本账户相容 pending：PendingPromoted
serverHit 与 keyHit 命中同一行且不可变字段相容：Duplicate
serverHit 命中而 keyHit 为空：Conflict
serverHit 与 keyHit 命中不同本地行：Conflict
任何命中行与权威载荷不相容：Conflict
```

- 不可变字段至少包括服务端消息 ID、发送者、客户端消息 ID、会话、类型、正文、回复目标、附件 ID 集合和提及用户 ID 集合；`MessageDto` 必须携带这些比较字段。
- `PendingPromoted` 只允许补齐本账户请求语义一致的 pending 行。`Conflict` 是数据完整性错误，必须回滚当前事务并记录，不能自动任选一行。
- 只有他人消息的 `Inserted` 可以增加未读或登记通知候选；`PendingPromoted` 只确认自己的发送状态。`Duplicate` 不得重复增加未读、创建通知候选或更新预览，但 History、已读推进和通知抑制可执行 `false -> true` 的单调观察型副作用并取消未派发候选。
- 任何路径都不得把 `IsRead` 或 `IsNotificationHandled` 从 `true` 重置为 `false`。发送状态固定为 `Sending -> Failed`、用户显式重试时 `Failed -> Sending`、权威响应或回声时 `Sending/Failed -> Sent`、以及终态 `Sent -> Sent`；迟到失败只记日志。

## 12.4 固定上界同步协议

请求与响应固定为：

```text
GET /api/sync?cursor={long}&snapshotUpperBound={long?}&limit={int?}
```

```csharp
public sealed record SyncResponse(
    IReadOnlyList<MessageDto> Messages,
    long NextCursor,
    long SnapshotUpperBound,
    bool HasMore);
```

- 首页省略 `snapshotUpperBound`。服务端在同一只读事务内读取 `MAX(Messages.Id)`（空表为 `0`）并完成本页查询，得到固定 `SnapshotUpperBound`。后续页原样携带该值；固定的是 ID 截止上界，不是在多个 HTTP 请求间持有同一事务。`limit` 省略时为 100，允许范围 `1..200`。
- 每页按当前权限重新过滤 `cursor < MessageId <= SnapshotUpperBound` 的同步候选。私有频道还要求 `MessageId > ConversationMembers.LastReadMessageId`；这只影响增量 Sync，不限制当前成员通过 History/Search 懒加载全部历史。
- 可见候选按 `MessageId ASC` 查询 `limit + 1` 条。有第 `limit + 1` 条时返回前 `limit` 条，`HasMore=true`，`NextCursor` 为本页最后一条 ID；没有更多可见消息时，`HasMore=false`，`NextCursor=SnapshotUpperBound`。即使本页为空或上界前全是无权限空洞，也必须跨到上界，不能空页死循环。
- 响应必须满足：消息 ID 严格递增且位于 `(cursor, NextCursor]`；`0 <= cursor <= NextCursor <= SnapshotUpperBound`；`HasMore == (NextCursor < SnapshotUpperBound)`；`HasMore=true` 时消息非空且 `NextCursor > cursor`；只要上界大于游标，下一游标就必须前进。
- `cursor < 0`、`limit` 不在 `1..200` 或 `snapshotUpperBound < cursor` 返回可诊断 `400`。首次游标或续页上界大于服务端当前最大消息 ID 时返回 `409 SyncCursorInvalid`；客户端不得静默夹断或归零。无状态服务端无法证明续页是否篡改上界，因此“原样回传”是受支持客户端不变量，授权仍由每页当前权限保证。
- 多页期间新提交且 ID 大于上界的消息不进入本轮，在下一轮拉取。第一版依赖单服务端实例、单 SQLite 主库、写事务串行化、`AUTOINCREMENT` 不复用已提交 ROWID和消息不可变；改变任一前提必须重新设计同步协议。

## 12.5 客户端逐页事务、重试与 single-flight

- 写本地数据库前先验证完整响应不变量。每页消息合并、会话预览、未读派生状态和 `LastSyncCursor=NextCursor` 必须在一个本地事务提交；任一非重复错误、协议错误或数据冲突使整页回滚，不得跳过坏消息后推进游标。
- 页面请求或本地提交失败时保留最后已提交游标。网络中断、超时、`429` 和可重试 `5xx` 用指数退避加抖动，以同一 `(cursor, SnapshotUpperBound)` 重试；`401` 只刷新一次令牌再重试同一页；`400` 或响应不变量错误停止并记录协议错误；`409 SyncCursorInvalid` 阻塞当前账户作用域并要求受控重建，不得清除 pending 或自动归零。
- 放弃轮次后，下一触发从最后已提交游标获取新上界；崩溃后丢失仅存在内存中的上界是安全的。循环到 `HasMore=false` 才是完整同步轮次，批量 Toast、声音和闪烁只能在相应本地提交之后执行。
- `Startup`、`Reconnect`、`WindowActivated`、`Periodic` 是 `SyncReason`。每个账户作用域只允许一个同步循环；运行中触发合并原因并只设置一次 pending rerun，当前轮结束后至多立即补跑一次。补跑时若窗口已激活取 `WindowActivated`，否则仍处启动恢复取 `Startup`，否则有重连触发取 `Reconnect`，其余取 `Periodic`。登出或切换账户必须取消旧循环。
- 每轮先获取权威会话列表并应用成员新增/撤权、静音和 `LastReadMessageId`，再拉消息页。第一版列表在一个服务端只读事务返回非分页全集并标记 `Complete=true`；只有响应校验和本地对账事务成功后，客户端才可依据缺失推断撤权。未来若分页，必须先引入所有页共享的固定 `MembershipSnapshotToken`，且仅完整快照返回 `Complete=true`；普通实时分页结果不得触发清理。

## 12.6 未读处理

四种消息来源的到达语义固定如下：

| 来源 | 本地合并 | 增加未读 | 通知决策 | 推进游标 | 更新会话预览 | 声音/闪烁 |
| --- | --- | --- | --- | --- | --- | --- |
| `Realtime` | 统一唯一键合并 | 仅他人消息首次入库、超过已读边界且非当前前台会话 | 提交后交给串行通知分发器 | 否 | 仅较新消息 | 成功通知后本消息最多一次 |
| `Sync` | 整页事务内统一合并 | 仅他人消息首次入库、超过已读边界且非当前前台会话 | 完整轮次后统一决策 | 随页面事务提交 `NextCursor` | 仅较新消息 | 每轮最多一次 |
| `History` | 懒加载并统一合并 | 否 | 直接标记已处理 | 否 | 否 | 否 |
| `SendResponse` | 确认或合并 pending | 否 | 直接标记已处理 | 否 | 仅较新消息 | 否 |

“当前前台会话”必须同时满足：主窗口可见、未最小化、拥有前台焦点，且当前打开的会话 ID 与消息会话一致。只有满足未读条件的 `Inserted` 才使会话和总未读数各增加一次；重复到达不得再次计数。

- `ConversationDto` 与 `LocalConversations` 都携带 `LastReadMessageId`。消息 `Id <= LastReadMessageId` 时不增加未读、不通知；该字段只表示已读边界，不参与历史可见性授权。
- `LastReadMessageId` 只能单调前进。`POST /api/conversations/{id}/read` 必须验证当前权限、目标消息属于该会话，并保存 `MAX(old, requested)`，不得接受任意极大 ID。
- 当前前台会话收到新消息时，在同一本地事务把消息标记为已读、通知已处理，并保存新的 `PendingReadThroughMessageId`，随后异步上报 read-through ID。
- 同一成员生命周期的有效边界为 `MAX(localLastReadMessageId, serverLastReadMessageId)`，会话刷新不得回退。只有服务端确认值不小于 pending 目标才可清除 `PendingReadThroughMessageId`；失败时以相同最大值幂等重试。撤权清理后的重新加入是新成员生命周期，以服务端新基线初始化。

第一版不实现对方已读回执。

## 12.7 账户作用域与本地隔离

- 稳定作用域键为 `AccountScopeId = Base64UrlNoPadding(SHA256(UTF8(CanonicalServerBaseUri + "\n" + CurrentUserId.ToString("D").ToLowerInvariant())))`。
- `CanonicalServerBaseUri` 必须是无 user-info、query、fragment 的绝对 HTTP(S) URI：scheme 和 IDN host 小写，移除默认 `80/443` 端口，由 `System.Uri` 消解 dot-segment，保留反向代理子路径，并规范成一个尾斜杠。
- 数据库目录、缓存目录、Toast Group、激活参数和 `LastSyncCursor` 只使用同一个 `AccountScopeId`，不得各自序列化服务器与用户元组。切换服务器、账户或登出后，旧作用域的消息、游标、未读、缓存与通知状态都不能复用。
- Toast 激活处理器必须先校验目标 `AccountScopeId` 与当前身份，再确认目标会话的当前权限；旧账户通知或迟到点击不得打开当前账户中碰巧同 ID 的内容。

## 12.8 私有频道历史、加入与撤权

- 用户加入或重新加入私有频道后，只要当前仍是成员，就可经 History/Search 查看全部历史；历史只在打开或定位时懒加载，不全量回填，不增加 `JoinedAtMessageId` 等可见性水位，也不回退全局同步游标。
- 添加成员、读取该会话当前 `MAX(Messages.Id)`（无消息为 `0`）并写入 `ConversationMembers.LastReadMessageId` 必须处于同一服务端写事务。该值只是不产生加入前未读与通知的基线。重复添加当前有效成员是幂等 no-op，不得重置边界；移除后重新加入才写新加入时间与新基线。
- 私有频道 Sync 排除 `MessageId <= LastReadMessageId`，所以加入前历史只经 History/Search 返回；客户端也防御性地把任何来源带回的旧消息标记为已读且通知已处理。加入后的消息按普通规则处理。
- `ConversationAccessRevoked(Guid conversationId)` 是尽力实时事件，不是授权真源。删除成员后，History、Search、附件、发送和后续 Sync 从撤权提交起按当前成员关系拒绝访问；相关 HTTP 请求返回带稳定错误码 `ConversationAccessRevoked` 的 `403`。每轮 Sync 前的权威会话全集对账覆盖事件丢失和离线场景；普通、无法归因撤权的 `403` 不触发破坏性清理。
- 私有成员 DELETE 的权威事务必须返回内部“是否真实删除”事实。只有实际删除并提交的请求向目标 user ID 的全部现有连接尝试一次 `ConversationAccessRevoked`；重复/并发删除仍可幂等返回 204，但不得重复推送。目标即使在提交后被禁用，也不因活跃用户过滤而丢掉对既有连接的清理信号；投递失败不回滚撤权、不改变 204、不在请求内重试。
- SignalR Group 只做路由优化。每次实时投递用当前权威成员快照；服务端观察到撤权提交后，不得把该用户放入新的接收集合。在撤权前已经排队或在途的帧仍可能迟到，客户端 deny-set 必须拒绝它们且不能复活缓存。
- 事件、权威列表缺失和稳定撤权 `403` 全部进入幂等 `PurgeConversationAccess`。它先更新当前 `AccountScopeId` 的线程安全进程 deny-set，随后不再因调用方取消而丢弃工作：先在 `LocalAppState` 独立提交 durable revocation intent，再以独立最小事务持久化 revoked tombstone、删除会话并清除 intent；消息入口、读取、UI、通知激活立即优先拒绝该会话。冷启动必须先幂等重放未完成 intent。
- intent 或 tombstone 首次持久化失败时，整个 `AccountScopeId` 立即进入 fatal fail-closed，本进程不再展示该作用域缓存。每次冷启动必须先成功获取并提交 `Complete=true` 的权威会话对账，才可加载或展示私有缓存；离线或对账失败只保持隐藏，不能据此删数据。权威登记必须在事务内先检查 tombstone，普通登记不得自行清除 tombstone 或 deny-set。
- tombstone 提交后才可重试细粒度清理：取消发送、History、上传下载和 UI/内存引用，删除消息、附件元数据、未读、通知候选与本地搜索数据，按会话 Group 清除 Toast，删除或重建可能包含该会话的账户 Summary，最后尽力删除物理缓存。任一步失败都不得移除 deny-set/tombstone 或恢复访问；只有权威列表明确确认重新加入才可清除 tombstone。
- 统一消息入口不得因未知 `ConversationId` 自动创建会话。未知或 revoked 数据先拒绝入库并触发权威对账，只有权威列表确认重新加入才恢复接收。离线设备无法远程擦除已落盘缓存是第一版已知限制。

---

# 13. 通知可靠性设计

## 13.1 通知唯一真源

逐消息 `IsNotificationHandled` 是唯一通知真源：

- `false` 表示尚未完成通知决策，或 Toast 提交遇到可重试临时失败。
- `true` 表示 Windows 已接受 Toast，或客户端已经明确决定不提醒。
- 自己发送、History、已读边界内消息、当前前台会话、会话静音、全局免打扰、Windows 通知被禁用和 `NotificationPolicy.None` 都是明确不提醒，在本地事务中置为 `true`，以后不补历史 Toast。
- 新候选随消息以 `false` 提交。外部 Toast、声音和闪烁只能在本地事务成功后发生；`IsRead=true` 或 `IsNotificationHandled=true` 不能被后到来源重置。

第一版普通频道默认开启通知；私聊、@ 当前用户和开启通知的频道消息在其他过滤条件通过后成为候选。

## 13.2 串行 NotificationCoordinator

- 单实例进程内只有一个串行 `NotificationCoordinator` 可以调用平台 Toast API；Realtime、Sync 和恢复路径只能提交明确的候选 ID，不能各自派发。
- 派发前再次确认消息仍未读、会话仍有效、未静音、未处于免打扰，且用户没有打开对应前台会话。不再符合条件时，以本地事务标记已处理。
- `PerMessage` 每成功提交一条就把该条置为 `true`；`Summary` 成功后在一个本地事务把该汇总覆盖的全部消息置为 `true`。部分成功只提交成功部分。
- Toast API 的可重试临时失败保持 `false`，留到后续后台同步或下一次启动恢复，不能紧循环。明确永久或配置性不可用时记录诊断并置为 `true`。
- 平台调用无异常只证明“已接受”，不证明用户看见。Toast 被接受后、置位前崩溃可能导致恢复时再提交一次；第一版接受这个 at-least-once 窗口，稳定 Tag/Group 只用于降低用户可见重复，不能宣称严格 exactly-once。

## 13.3 同步轮次候选与原子 gate

同步轮次维护两组 ID：本轮由 Sync 首次插入或同步期间由 Realtime 首次插入的 `RoundCandidates`（保留首次来源），以及以前临时失败留下的 `RecoveryCandidates`。

- 同步运行期间，Realtime 候选不立即弹 Toast。协调器用同一把 gate 原子完成“关闭轮次、截取并清空 RoundCandidates、切换 Realtime 分流状态”：Realtime 在 gate 内看到未关闭就加入本轮，看到已关闭就走提交后即时派发，不存在丢失中间态。
- `NotificationCoordinator` 只能处理调用方提交的 ID。只有 Startup，或后台 Reconnect/Periodic 已成功提交权威会话对账后，才可扫描当前 `AccountScopeId` 的遗留 `false` 并显式构造 Recovery；Realtime kick 不得全表扫描或提前取走 Sync 候选。
- 完整轮次结束后统一去重和决策。Startup 处理 Round 与 Recovery 并集；后台 Reconnect/Periodic 在权威列表成功后即可处理并集，即使后续消息分页失败；前台与 WindowActivated 的 `None` 只处理 Round，Recovery 保持 `false`。
- 轮次失败或取消时，在同一 gate 按首次来源拆分：Realtime 首次插入的候选恢复即时串行派发并完成一次当前状态下的决策；Sync 首次插入的候选保持 `false` 并进入 Recovery。永久 `400`、协议冲突或本地 poison row 不得无限扣住真实实时消息。
- 最后一页提交后、通知决策前崩溃时，未处理项按恢复规则处理。

## 13.4 批量策略

| 场景 | 候选处理 |
| --- | --- |
| `Startup` | 有候选时只发一条 `Summary` |
| `WindowActivated` | `None`；仅更新未读并把本轮候选标记为已处理 |
| `Reconnect` / `Periodic`，窗口在前台 | Round 使用 `None`；Recovery 保留 |
| `Reconnect` / `Periodic`，窗口不在前台且候选数 `1..10` | `PerMessage` |
| `Reconnect` / `Periodic`，窗口不在前台且候选数 `>10` | 一条 `Summary` |

阈值只统计过滤掉本人、已读、静音、免打扰和当前前台会话后的实际候选。同步一轮即使提交多条 Toast，声音和任务栏闪烁最多各触发一次；同步轮次外的 Realtime 单条消息按单条处理。

## 13.5 Windows Toast 身份与激活目标

- `PerMessage` Group 为 `Base64UrlNoPadding(SHA256(UTF8(AccountScopeId + "\n" + ConversationId.ToString("D").ToLowerInvariant())))`，Tag 为十进制 `ServerMessageId`。
- `Summary` Group 为 `Base64UrlNoPadding(SHA256(UTF8(AccountScopeId + "\nsummary")))`，Tag 固定为 `unread-summary`。
- 按会话 Group 清除 Toast 不依赖本地消息行仍存在；读完或撤权时可清除整个会话的陈旧 Toast。账户 Summary 则删除，或按当前未读重建。
- 激活目标是判别联合：`MessageTarget(AccountScopeId, ConversationId, MessageId)` 或 `UnreadOverviewTarget(AccountScopeId)`。跨会话 Summary 必须使用后者，不能伪造单一消息。
- `MessageTarget` 通过账户与权限校验后，打开主窗口、取消最小化、激活窗口、切换会话并定位消息；`UnreadOverviewTarget` 打开未读总览。无效、旧账户或已撤权目标只记录诊断，不展示缓存内容。

## 13.6 声音、任务栏与托盘

- Toast 成功后才尝试提示音和 `FlashWindowEx`。声音或闪烁失败只记日志，不能把已成功 Toast 改回未处理，也不能重试 Toast。
- 主窗口不在前台且存在需要提醒的新消息时可闪烁；窗口被激活或用户打开对应会话时停止。
- 托盘必须显示图标、总未读数和连接状态，并提供打开主窗口与彻底退出。关闭主窗口默认只隐藏，保留进程、SignalR 和通知能力；彻底退出后不要求实时通知，下次启动由同步恢复。

## 13.7 单实例与激活转交

同一台电脑同时只能有一个 RelayCove.Client 主实例。通知激活探针在实现阶段必须比较 `AppInstance.RedirectActivationToAsync` 与 `Mutex + Named Pipe`，并固定满足以下行为的方案：

- 没有已有实例时，新主实例完成账户上下文初始化后在本地处理完整激活目标。
- 已有实例时，次实例通过选定 IPC/`AppInstance` 转交完整目标，收到主实例确认后退出，不能只尝试激活窗口。
- 激活处理幂等；重复目标不得创建第二窗口或重复导航。主实例必须重新校验 `AccountScopeId`、当前身份和会话权限。

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

管理员功能放在对应服务器自带的轻量网页面板中。

不开发独立 SPA、前端构建链或聊天 Web 端；管理页由现有 ASP.NET Core
Server 直接提供，并复用同一数据库与管理业务服务。

入口：

```text
https://<server>/<path-base>/admin/
```

只有当前正常管理员账号可以登录。Windows 客户端的旧管理入口只在网页面板
完成生产验证前保留，验证通过后移除。

## 17.2 第一版管理员功能

必须实现：

- 创建账号；
- 禁用账号；
- 删除账号（不可恢复的逻辑退役，保留历史引用）；
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
最近一次脱敏错误类别与时间
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
先冻结同步、幂等、通知和私有权限契约，再创建可构建解决方案、项目结构和真实验证脚本。
```

任务：

1. 接受 `DEC-003` 并统一规范性文档；
2. 创建 `RelayCove.sln`；
3. 创建 Client、Server、Shared、Updater 项目和对应测试项目；
4. 开启 Nullable；
5. 配置基础日志；
6. 配置 `.gitignore`；
7. 创建真实 `Fast` / `Full` 验证脚本；
8. 确保 Debug、Release 和测试均可运行。

验收：

```text
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
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
9. 定义 IncomingMessageSource、SyncReason 与 NotificationPolicy；
10. 定义稳定错误响应和 `SyncCursorInvalid`、`IdempotencyKeyReuse`、`ConversationAccessRevoked` 错误码。

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

默认管理员不是仓库内置账号：bootstrap 默认关闭，凭据由部署环境一次性注入，只在已迁移且 Users 表为空时创建；成功后必须移除凭据。非空库永不由 bootstrap 自动覆盖、提权或改密。阶段 2 的管理员接口只实现创建用户，禁用、删除和重置密码仍在阶段 11 实现。

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
8. 在添加成员事务中初始化 `LastReadMessageId`；
9. 实现 `Complete=true` 权威会话全集与撤权错误码；
10. 权限校验。

验收：

```text
公共频道所有人可见
私有频道只有成员可见
私聊会话只对两人可见
无权限用户不能访问会话
加入或重新加入后可懒加载全部历史，但加入前历史不计未读
撤权提交后相关资源立即返回稳定 403
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
4. 实现 INSERT-first 幂等：201 新建、200 重放、409 键复用冲突；
5. 实现历史消息接口；
6. 实现消息 around 查询；
7. 实现 read 接口。

验收：

```text
能发送文字消息
重复提交同一 ClientMessageId 不会生成重复消息
并发相同提交只推送一次，不同载荷重放返回 409
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
5. 推送 `ConversationAccessRevoked` 并拒绝迟到消息复活缓存；
6. 客户端接收 NewMessage；
7. 每次重连后按权威权限重新加组；
8. 客户端显示连接状态。

验收：

```text
A 发消息
B 在线时立即收到
B 无权限会话不会收到
撤权后即使旧连接有在途帧也不会恢复本地访问
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
5. 按 `AccountScopeId` 隔离本地数据库、缓存和 `LastSyncCursor`；
6. 分离 `LocalId` 与可空唯一 `ServerMessageId`；
7. 实现四来源唯一合并路径；
8. 实现固定 `SnapshotUpperBound` 的逐页事务同步；
9. 实现 single-flight、重试和受控游标错误；
10. 实现权威会话对账、撤权清理和单调未读边界。

验收：

```text
断网期间的消息恢复网络后可补拉
消息不重复显示
未读数量正确
客户端重启后仍能显示最近消息
权限空洞不会造成空页死循环或游标停滞
pending、响应、回声和补拉最终只有一条本地消息
切换账户或服务器不会复用旧作用域数据
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
10. 实现串行 `NotificationCoordinator` 与恢复候选；
11. 通过探针选择 `AppInstance` 或 `Mutex + Named Pipe` 并实现完整激活转交。

验收：

```text
B 最小化时收到通知
B 关闭窗口进入托盘后仍收到通知
收到消息时任务栏闪烁
点击通知打开对应会话
再次启动客户端不会出现第二个实例
Realtime、Sync 与恢复扫描不会并行重复提交同一候选
旧账户或已撤权通知无法打开缓存内容
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
普通用户无法登录管理员网页
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
B 按 SyncReason 和候选数得到唯一确定的通知策略

B 彻底退出
A 给 B 发消息
B 不要求实时通知
B 下次启动后能补拉未读

切换账户后点击旧 Toast
不能打开当前账户同 ID 会话

私有频道撤权后点击陈旧 Toast
不能显示已撤权缓存
```

自动化测试必须覆盖 Startup 汇总、WindowActivated 不弹历史通知、前后台 Reconnect/Periodic、阈值 `10/11`、Toast 临时失败恢复、同步失败时 Realtime 候选解闸，以及串行协调器的并发边界。真实 Windows 行为仍需安装态人工验收。

## 21.2 消息验收

```text
发送文字成功
发送多行成功
发送失败可重试
重复提交不会重复生成消息
并发相同请求得到一个 201、一个 200 且只推送一次
相同幂等键不同载荷返回 409 IdempotencyKeyReuse
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

## 21.6 同步、幂等与权限契约测试

以下 Given/When/Then 场景必须在对应实现切片中转成 xUnit 或集成测试：

1. 全局 ID 含大量无权限空洞或整页无可见消息时，最终 `NextCursor` 仍到达快照上限且不死循环。
2. 多页同步期间新提交消息不进入旧快照，在下一轮可拉到。
3. 本地某条消息写入失败时整页和游标均回滚；重试后不丢失、不重复派生副作用。
4. Realtime、Sync、SendResponse 以任意顺序并发到达，最终只有一条本地消息、一次未读、至多一次通知决策；用 barrier 覆盖“同步轮次关闭”与 Realtime 到达的原子边界。
5. pending 消息没有服务端 ID 时仍可持久化；响应或回声后只提升同一行。
6. 两个并发相同请求只产生一条服务端消息：一个 `201`、一个 `200`、只允许一次推送；相同键不同载荷返回 `409`。
7. 切换账户或服务器不会复用旧 `LastSyncCursor`；游标超出服务端最大 ID 时显式失败，不静默跳过。
8. Startup、WindowActivated、前后台 Reconnect/Periodic、阈值 `10/11` 和 Toast 临时失败分别得到确定策略。
9. 客户端游标为 `50`、私有频道加入前消息为 `60..90`、加入基线为 `90` 时，Sync 不返回 `60..90`，History/Search 可按需返回全部历史，`91` 后的新消息正常同步与提醒；重新加入同理且不全量回填。
10. 撤权事件丢失或设备离线后，权威列表或稳定撤权 `403` 仍触发本地收敛；服务端从撤权提交起拒绝所有相关资源访问。
11. SignalR 重连后按当前权限重新加组；已撤权会话不会因旧组状态继续接收。
12. 重复添加当前成员不重置已读边界；较小服务端 LastRead 不覆盖本地 pending read-through；撤权后迟到 Realtime/History 不复活缓存。
13. History 再次命中已由 Realtime 插入的未读行时，不重复到达型副作用，但可单调置为已读或通知已处理并取消未派发候选。
14. 权威会话列表读取期间成员新增、移除或排序变化，第一版单事务全集仍形成一致快照；未来普通分页结果不得触发 Purge。
15. Realtime 在 Sync 期间到达而该轮随后永久 `400` 或本地提交失败时，Realtime 候选仍解闸并完成一次通知决策，Sync 候选留待恢复。
16. 清理任一步失败仍保持 deny-set/tombstone；会话 Group 可清除已无本地行的 Toast；旧账户 Toast、迟到点击和重复激活不能打开当前账户内容或创建第二窗口。
17. revoked tombstone 首次 INSERT 失败后立即崩溃并离线重启时，冷启动权威对账 gate 仍阻止旧私有缓存显示。

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
聊天 Web 端
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
