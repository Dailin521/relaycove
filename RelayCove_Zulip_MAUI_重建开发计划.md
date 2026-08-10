# RelayCove 重建计划：.NET MAUI 客户端 + Zulip 服务端

文档状态：Stage 21 执行基线
目标版本：`2.0.0-alpha.1`
首发平台：Windows 11 x64
目标框架：`net10.0-windows10.0.19041.0`
最后更新：2026-08-10

## 1. 决策摘要

RelayCove 放弃自研聊天服务端、ASP.NET Core/SignalR 协议、旧 Shared DTO、Updater 与安装器，重建为直接连接 Zulip 的纯客户端。Zulip Server 是用户、权限、频道、消息和实时事件的唯一事实源；RelayCove 只负责客户端体验、本地缓存和平台集成。

本阶段直接在本地 `main` 实施，但不推送、不创建版本标签、不发布、不修改目标 Zulip 主机。旧 Git 历史和 `v1.0.0-rc.25` 标签保留为回滚点。删除使用普通 Git 变更，不使用 orphan、`reset --hard`、force-push。

唯一允许沿用的旧产品资产是原始 `RelayCove.ico`：

- 路径：`src/RelayCove.App/Resources/AppIcon/RelayCove.ico`
- 字节数：65,044
- SHA-256：`07906CE7D87860C4A15DDD6F904DA722F7BBC3C882DC32FD1D285A78B1161B52`

不复制旧截图、`ClientIcons.xaml`、模板品牌图、Zulip 商标或 Zulip 客户端素材。

## 2. 已验证事实与未验证门禁

### 2.1 已验证

| 项目 | 证据 |
|---|---|
| 旧版回滚点 | `main` 原始 HEAD 为 `46c9f0e74068a29f4fb14180f426f1fbf30cef36`，标签 `v1.0.0-rc.25` 存在 |
| 旧版 Fast 基线 | Shared 70、Server 353、Client 1272、Updater 38，共 1733/1733 测试通过 |
| 本机 SDK | .NET SDK `10.0.101` |
| MAUI workload | `maui-windows 10.0.1/10.0.100` 已安装 |
| 发布 spike | 仓库外最小 MAUI 应用可 unpackaged、自包含发布为 `win-x64` |
| Windows SecureStorage | 当前开发机完成写入、跨启动读取和删除；文件内容未发现明文 sentinel |
| SQLite native | WAL、事务和 `e_sqlite3.dll` 在 spike 发布目录中工作 |
| 目标 Realm | `https://hklight.2000521.xyz` 返回 Zulip 12.1、feature level 500、邮箱密码认证可用 |
| 图标完整性 | 迁移前后字节数与 SHA-256 一致 |

目标主机的部署、Nginx、备份和恢复事实以 [主机配置索引](E:/GitHubProject/server-admin/servers/zulip-hklight/README.md) 及其链接文档为准。本项目不复制主机秘密，也不修改主机配置。

### 2.2 未验证，禁止误报通过

| 门禁 | 状态 | 完成条件 |
|---|---|---|
| 干净 Windows 11 x64 VM 启动 | 未验证 | 在未安装 .NET 与 Windows App SDK Runtime 的 VM 中运行最终 ZIP |
| 真实 Realm Live 测试 | 未验证 | 两个专用账号 API key、隔离私有频道和显式写入授权齐全 |
| 人工密码登录 | 未验证 | 专用测试账号在最终 MAUI UI 中完成一次登录 |
| 最终视觉验收 | 未开始 | 用户提供原型后另立阶段 |
| 签名/安装器/公开发布 | 不在范围 | 需要单独授权和发布方案 |

任何门禁失败都必须保留证据并停止对应交付声明，不得静默切换凭据后端、关闭 TLS 校验或扩大测试目标。

## 3. 产品范围

### 3.1 MVP 必须完成

1. 可编辑 Realm 的邮箱密码登录，默认 `https://hklight.2000521.xyz`。
2. 单一活动账号；正常关闭后可用 SecureStorage 自动恢复，离线读取同账号缓存。
3. 已订阅频道、频道话题、1:1 私信、群组私信与 self-DM 导航。
4. 每页 50 条的历史查询与向上分页。
5. raw Markdown 文本显示和发送，不渲染服务端 HTML，不使用 WebView。
6. 已读/未读、实时事件、断线恢复、限流和事件队列重建。
7. SQLite 账号隔离缓存；订阅撤权后立即从导航移除并清除频道数据。
8. Zulip 式本地回声：500 ms 隐藏窗口、Waiting、10 s WaitExpired、明确 Failed、事件对账。
9. 被动处理其他客户端产生的消息编辑、频道/话题移动和批量删除事件。
10. 明确确认的清除本地缓存操作。

### 3.2 本阶段明确不做

频道管理、附件、反应、主动编辑/删除入口、搜索、typing、presence、通知、push、SSO、多账号、AI、自动更新、安装器、MSIX、签名、Android、iOS、Mac Catalyst 和 Linux。

后续平台可以复用 Core、Zulip.Client 与 Data，但必须另行增加目标框架、图标、签名、后台生命周期和发布验证。本阶段不声称可交付这些平台；Linux 不支持。

## 4. 架构和依赖边界

```text
RelayCove.App
  ├─> RelayCove.Core
  ├─> RelayCove.Zulip.Client ─> RelayCove.Core
  └─> RelayCove.Data         ─> RelayCove.Core
```

| 工程 | 责任 | 禁止事项 |
|---|---|---|
| `RelayCove.App` | MAUI XAML、ViewModel、Windows composition root、SecureStorage、Preferences | 不直接使用 HttpClient/SQLiteConnection/Zulip DTO |
| `RelayCove.Core` | 领域模型、公共契约、单通道 reducer、会话/同步用例 | 不引用 MAUI、SQLite、JSON DTO |
| `RelayCove.Zulip.Client` | 安全 HttpClient、Basic Auth、REST/事件队列、DTO 映射 | 不保存凭据、不操作数据库、不暴露 JSON 给 UI |
| `RelayCove.Data` | 每账号 SQLite、迁移、事务、mutation lane | 不存 API key、queue_id、last_event_id 或 outbox |

UI 只依赖 `IClientSession`。所有 register、历史、实时事件、发送对账和 SQLite 写入通过单一 mutation lane；UI 线程不执行数据库 I/O。

Windows 应用标识固定为 `com.relaycove.client`。MAUI/Windows 资源元数据只接受数字版 `ApplicationDisplayVersion`，因此资源显示版本使用 `2.0.0`，程序集 `InformationalVersion` 保留完整预发布语义 `2.0.0-alpha.1`；ZIP 文件名也使用完整预发布版本。

## 5. 冻结公共契约

### 5.1 身份与会话

- `RealmEndpoint`：仅接受没有 userinfo、query、fragment 的绝对 HTTPS origin；路径只允许空或 `/`。
- `AccountId`：`SHA-256(normalized realm origin + "\n" + Zulip user ID)` 的 64 位小写十六进制。
- `CredentialEnvelope`：规范 Realm、邮箱、Zulip user ID、API key；`ToString()` 和日志必须完全脱敏。
- `ConversationKey.ChannelTopic(channelId, topic)`：topic 允许空字符串以兼容 Zulip 空主题能力。
- `ConversationKey.DirectMessage(sorted otherUserIds)`：移除当前用户后排序；空集合表示 self-DM；值相等且不可变。

### 5.2 网关

`IZulipGateway` 固定提供：

```text
ProbeRealmAsync
AuthenticateAsync
RegisterAsync
GetEventsAsync
GetHistoryAsync
GetTopicsAsync
SendAsync
MarkReadAsync
DeleteQueueAsync
```

协议以 [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml) 和脱敏夹具为依据。JSON 使用 `System.Text.Json`，未知字段忽略，未知事件安全推进 cursor，不让事件循环崩溃。

### 5.3 存储

`IAccountStore` 提供账号枚举、初始化、迁移、加载、消息分页、register 快照替换、领域事件批量事务、订阅清理、缓存锁定/解锁和显式清缓存。

数据库路径固定为：

```text
<AppDataDirectory>/accounts/<AccountId>/relaycove.db
```

必须启用外键、WAL、事务和路径校验。数据库保存账号元数据、用户、订阅、话题、近期私信、消息、flags、未读和 schema version；不保存 API key、密码、事件队列、事件 cursor 或 outbox。

### 5.4 客户端会话

`IClientSession` 是 ViewModel 唯一入口，负责恢复、登录、注销、会话选择、话题读取、分页、发送、标已读、清缓存和连接状态。所有命令支持取消；重复命令必须串行化或拒绝，不允许并发破坏同一 session。

## 6. 认证和安全设计

### 6.1 登录顺序

```text
输入 Realm/email/password
  -> 无凭据 GET /server_settings
  -> 校验 HTTPS、无重定向、FL>=500、is_incompatible=false、email auth
  -> POST /fetch_api_key（密码最后一次出现）
  -> Basic Auth GET /users/me，核对 user ID
  -> SecureStorage 原子保存 CredentialEnvelope
  -> 初始化/迁移并解锁账号缓存
  -> POST /register，事务应用快照
  -> 发布 UI state 并启动 event loop
```

若 SecureStorage 写入失败，登录整体失败、删除半成品凭据并锁定缓存。密码不得进入字段、异常、HTTP 日志、测试快照或持久存储。

### 6.2 SecureStorage

- 固定一个 key 保存一个 JSON `CredentialEnvelope`。
- Windows 采用 MAUI SecureStorage/DataProtectionProvider；unpackaged 应用的 `securestorage.dat` 是支持路径的一部分。
- 读取失败、文件损坏或系统密钥变化时捕获异常、删除不可用 envelope、锁定缓存并要求重新认证。
- 凭据存在的正常重启允许离线读取该账号缓存。
- 显式注销先停止事件循环，再删除凭据并锁缓存；缓存保留但 UI 不可浏览。

### 6.3 网络安全

- 生产构造器内部固定 `AllowAutoRedirect=false`；测试 handler 不向 App 暴露。
- 重定向只显示“请输入规范 Realm”，绝不把密码/API key 转发到新 origin。
- TLS 使用系统证书链，不提供跳过校验的开关。
- `401` 立即停止网络、删凭据、锁缓存并进入 `ReauthRequired`，不循环重试。
- `429` 读取并遵守 Zulip `retry-after`；只重试幂等读和 register。
- 消息 POST 在超时或结果不确定时不自动重试。
- 日志只含固定操作名、HTTP 状态、耗时和受控错误码；不含 Realm、URL 参数、邮箱、Authorization、queue ID、local ID、正文或服务器错误正文。

## 7. Zulip 12.1 协议实现

### 7.1 Register

`POST /register` 固定请求 raw Markdown 与最小数据：

```text
apply_markdown=false
client_gravatar=false
include_subscribers=false
idle_queue_timeout=3600
event_types=[message,subscription,realm_user,stream,update_message,
             delete_message,update_message_flags,realm,heartbeat,restart]
fetch_event_types=[subscription,realm_user,realm,recent_private_conversations]
```

能力至少声明：`notification_settings_null`、`bulk_message_deletion`、`user_avatar_url_field_optional`、`user_list_incomplete`、`empty_topic_name`。

必须读取 `queue_id`、`last_event_id`、`event_queue_longpoll_timeout_seconds`、`max_message_length`、`max_topic_length`。`idle_queue_timeout_secs` 是队列空闲生命周期，不得替代 HTTP long-poll timeout。快照在单个数据库事务完成后才发布 UI。

### 7.2 Events

- `GET /events` 发送 `queue_id`、`last_event_id`、`dont_block=false`，HTTP timeout 使用 register 返回值。
- event ID 只要求递增，不要求连续；相同 ID 的多个领域效果作为一个原子组应用。
- `queue_id` 与 cursor 只存在当前进程，不跨启动恢复。
- `heartbeat` 只推进 cursor。
- 完全未知事件只记录固定脱敏码。
- `update_message` 更新 raw 内容并处理 `message_ids` 批量移动。
- `delete_message` 处理 `message_ids` 批量删除与未读引用。
- `subscription remove` 立即删除导航、话题、消息与未读；`stream` rename/archive/delete 更新本地订阅。
- 已识别但不在 MVP 的 subscription 属性以显式 ignored 事件推进 cursor，不伪装成协议故障。

`BAD_EVENT_QUEUE_ID`、服务器重启或长时间离线后重新 register。新快照与旧订阅在事务内对比，撤权频道立即清理；其他缓存标记/视为可按访问刷新。重连采用有上限指数退避和抖动，避免紧循环。

### 7.3 历史、话题和已读

- 历史页固定 50 条。
- 首页：`anchor=newest`。
- 旧页：当前最小 message ID，`include_anchor=false`。
- 频道 narrow：`channel` ID + `topic`。
- 私信 narrow：`dm` + 规范参与者 ID；self-DM 使用当前 user ID。
- 频道话题使用 `/users/me/{channel_id}/topics` 并允许空主题。
- 已显示页通过 `/messages/flags/narrow`、会话 narrow 和 `is:unread` 标记最多 50 条；服务成功后才更新本地 flags。

### 7.4 发送与 outbox

- 每个进程由 `IClientSession` 生成递增数字字符串 `local_id`；网关按 Zulip 合约把它当不透明字符串。
- `queue_id` 与 `local_id` 成对发送，它们不是幂等键。
- 频道发送仅允许当前已订阅频道，topic 必填（空主题按 Zulip 能力仍是合法字符串），正文/topic 用 register 上限校验。
- DM recipient 使用排序后的用户 ID；self-DM 发给当前 user ID。
- 初始 outbox 为 `Hidden`；500 ms 无事件则 `Waiting`；HTTP 超过 10 s 为 `WaitExpired`；明确 API 拒绝为 `Failed`。
- 匹配 `local_message_id` 的事件立即替换 outbox。
- HTTP 先返回 message ID 而事件缺失时，按 ID/narrow 定向拉取对账。
- outbox 不写 SQLite。离线、超时和模糊失败不自动重发；用户只能把失败正文恢复到编辑框并显式再次发送，UI 提示可能重复。

## 8. SQLite schema 和一致性

Schema v1 至少包含：

```text
schema_info
account_metadata
users
subscriptions
topics
recent_dm
messages
unread_counts
unread_state
```

一致性规则：

1. 所有数据库操作进入单 reader 的后台 channel，避免 UI 线程 I/O 和写竞争。
2. register 替换、事件批次、历史页和发送对账均使用事务。
3. 外键级联清理频道话题与消息，领域 reducer 同时清理未读引用。
4. mutation 失败整批回滚，内存状态只在数据库成功后发布。
5. SQLite 是缓存，不迁移旧 RelayCove 业务数据，不成为第二事实源。
6. 锁定是应用级 fail-closed 隔离，不等同磁盘加密；文档和 UI 必须说明这一点。
7. 清缓存只删除经过 64 位 AccountId、父路径、非 reparse-point 校验的精确账号目录。
8. `SQLitePCLRaw.bundle_e_sqlite3` 显式固定为 `2.1.12`；本地测试要求实际加载的 SQLite 不低于 `3.50.2`，避免重新引入 `CVE-2025-6965` 影响的原生版本。

## 9. 最小 MAUI Shell

本阶段 UI 只冻结功能，不冻结最终视觉：

```text
LoginPage
  Realm / Email / Password / Login / categorized error

MainPage
  Connection status
  Channel list -> Topic list
  Direct-message list
  Virtualized CollectionView messages
  Raw Markdown text labels
  Composer + Send
  Logout / Clear local cache
```

ViewModel 使用 CommunityToolkit.Mvvm，只调用 `IClientSession`。code-behind 仅处理焦点、滚动和 View 生命周期。消息列表用 `CollectionView` 虚拟化，切换会话先显示缓存，再进行网络刷新；页面离开时取消旧请求，避免过期结果覆盖当前页面。

登录错误分类：不兼容 Realm、认证失败、限流、离线、凭据存储失败。最后 Realm 可用 Preferences 保存为非敏感配置；密码字段完成登录后立即清空。

## 10. 实施切片与完成定义

### Slice A：删除与平台门禁

- 旧 Fast 基线、HEAD/tag、icon hash 形成证据。
- 安装/验证 MAUI workload。
- 仓库外 spike 证明 unpackaged self-contained、SecureStorage、SQLite/native。
- 最终包必须补做干净 Windows 11 VM；未完成前只标本机通过。

### Slice B：干净骨架

- 删除 WPF/Server/Shared/Updater/旧测试/安装器/旧产品文档。
- 建立四层解决方案、五个测试项目、版本和 icon。
- 重写 README、计划、治理、状态与 Stage 21 任务记录。

### Slice C：登录纵向切片

- Realm 探测、能力门禁、密码换 API key、当前用户验证。
- SecureStorage、自动恢复、离线缓存、注销锁定和分类错误。
- 单元测试禁止真实网络。

### Slice D：接收和离线纵向切片

- schema/migration、register、事件循环、导航、话题、历史分页。
- queue rebuild、订阅撤权、编辑/移动/删除、401/429/断网。
- 相同账号重认证、缓存锁定和精确清理。

### Slice E：消息与交付

- 频道、1:1、群组、self-DM 发送和 outbox 对账。
- 当前展示页已读、最小 MAUI Shell、ViewModel 取消/去重。
- Fast、Full、一次显式 Live、干净 VM、独立复核、ZIP/SHA-256。

每个切片只有在代码、测试、文档、独立复核和限制说明齐全时完成。真实写入、推送、合并、标签和发布必须另行授权。

## 11. 自动化验证

### 11.1 Fast

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

执行格式/静态检查、Debug build、Core/Zulip/Data/App 单元测试。不得连接外部网络或运行 LiveTests。

### 11.2 Full

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

执行 Fast、Release build、本地全部测试、仅 MAUI app 项目 publish、ZIP 内容检查、icon/native runtime 检查和秘密扫描。禁止对 solution 执行 `dotnet publish`。

每个交付快照另执行 `dotnet list RelayCove.sln package --vulnerable --include-transitive` 并保存结果；任何已知漏洞都阻断交付。该检查依赖 NuGet 在线公告，不伪装成离线 Fast 门禁。

固定发布参数：

```text
project: src/RelayCove.App/RelayCove.App.csproj
framework: net10.0-windows10.0.19041.0
RuntimeIdentifierOverride=win-x64
WindowsPackageType=None
WindowsAppSDKSelfContained=true
self-contained=true
artifact: RelayCove-2.0.0-alpha.1-win-x64.zip
```

### 11.3 Live

```powershell
pwsh ./scripts/verify.ps1 -Mode Live
```

必须同时提供目标 Realm、测试账号 A/B 的 email/API key、写入确认变量和 recipient allowlist；任意缺失都 fail-closed。日常 Live 复用 API key，不调用 `/fetch_api_key`。

一次性 bootstrap 只能在独立高权限凭据和额外授权下创建私有频道 `relaycove-client-e2e`。已存在同名频道时，只有它仍为私有且成员集合精确等于两个测试账号才可复用，否则停止且不修改。

每次运行使用唯一 `run-<UTC>-<random>` topic，验证 A 发频道消息、B 经事件接收、历史回查和标已读；真实私信只允许 A↔B，发送前必须对解析后的 user ID 做代码级 allowlist。群组私信只用本地三用户夹具。删除自己的 event queue 验证 queue rebuild；不制造 429、不撤销真实账号、不操作其他频道、不自动清除消息。

## 12. 测试矩阵

| 层 | 必测场景 |
|---|---|
| Core | Realm 规范化、AccountId、DM 值语义、同 ID 原子组、跳号/重放、撤权、移动/编辑/删除、flags、outbox 计时/失败 |
| Zulip.Client | 无凭据 probe、重定向拒绝、Basic Auth 脱敏、12.1 DTO、未知字段、narrow、self/group DM、401/429/BAD queue、local echo |
| Data | schema/WAL/FK、迁移、事务回滚、账号隔离、锁定、相同账号解锁、撤权级联、并发 mutation、精确清缓存、无 secret、SQLite 原生版本安全下限 |
| Session | 登录/恢复/注销、register 竞态、事件重放、queue rebuild、断网/限流/401、分页、取消/重复命令、outbox HTTP/事件先后竞态 |
| App | SecureStorage 成功/失败/损坏、最后 Realm、错误分类、命令状态、UI 不直接 I/O、密码清空 |
| Package | self-contained runtime、`e_sqlite3.dll`、icon、无 db/log/secret/env、ZIP SHA-256、传递依赖漏洞审计 |
| Live | 专用频道收发、事件接收、历史、已读、删队列重建、严格 recipient allowlist |

## 13. Windows 交付验收

最终 ZIP 必须在干净 Windows 11 x64 VM 验证：

1. 无 .NET/Windows App SDK Runtime 环境直接启动。
2. 首次登录、频道/话题/DM 文本收发。
3. 关闭重启自动恢复。
4. 断网时读取缓存，联网后恢复。
5. 注销后无法浏览缓存。
6. 相同用户重新认证后恢复缓存。
7. 显式清缓存后账号目录精确删除。

ZIP 不得包含 API key、密码、Live 环境变量、SQLite 数据库、日志、测试夹具秘密或旧产品素材。只生成 unsigned 内部预发布 ZIP，不创建 MSIX、安装器、签名或自动更新。

## 14. 风险与控制

| 风险 | 级别 | 控制 |
|---|---|---|
| API key 泄露等同账号接管 | 高 | SecureStorage、日志/包 secret scan、单 envelope、401 清理 |
| 自动重定向泄露凭据 | 高 | 生产 handler 固定禁用 redirect，3xx fail-closed |
| 模糊发送结果导致重复 | 高 | 不自动重发、local echo 对账、明确用户提示 |
| 队列过期/服务器重启 | 高 | 丢弃旧 queue/cursor、re-register、事务 reconcile、退避抖动 |
| 订阅撤权后缓存泄露 | 高 | register 对比 + FK/领域级清理 + 导航立即移除 |
| SQLite 明文消息 | 中高 | 用户目录权限、明确说明、锁定 UI、显式清缓存；MVP 不宣称加密 |
| MAUI unpackaged 运行时缺失 | 高 | self-contained publish + 干净 VM 门禁 |
| Zulip 升级导致契约漂移 | 中高 | FL>=500 门禁、未知字段兼容、升级后重新跑 Live |
| UI 长列表性能 | 中 | 50 条分页、CollectionView 虚拟化、后台 DB lane、可取消请求 |
| 范围膨胀 | 中 | 冻结不做清单，新增能力必须新 stage/ADR |

## 15. 完成标准与授权边界

Stage 21 只有在下列条件全部满足时才能标记完成：

- 旧实现只剩 Git 历史，工作树中唯一旧产品资产是 hash 不变的 icon。
- Core、Zulip.Client、Data、Session、App 和 package tests 全部通过。
- Fast 与 Full 有当前提交证据。
- 一次显式 Live 有专用账号/频道证据。
- 最终 ZIP 通过秘密扫描、SHA-256 清单和干净 VM 验收。
- 认证、协议、同步、缓存撤权、outbox 和发布各完成独立只读复核；P0/P1 已解决。
- README、本文、STATUS、WORKFLOW、Stage 21 记录与实际命令一致。
- 所有未验证项显式列出，不用“预计”“应当”代替证据。

以下动作不在当前授权内：推送 `main`、合并远端、创建标签、上传/公开发布 ZIP、修改或停用 Zulip/旧服务、使用生产凭据、删除旧 `%LOCALAPPDATA%\RelayCove`。如需执行，必须获得新的明确授权并先解析、展示和确认精确目标。

## 16. 官方依据

- [Zulip 客户端库说明](https://docs.zulip.com/api/client-libraries)
- [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml)
- [注册事件队列](https://docs.zulip.com/api/register-queue)
- [获取实时事件](https://docs.zulip.com/api/get-events)
- [获取历史消息](https://docs.zulip.com/api/get-messages)
- [发送消息](https://docs.zulip.com/api/send-message)
- [按 narrow 更新 flags](https://docs.zulip.com/api/update-message-flags-for-narrow)
- [MAUI SecureStorage](https://learn.microsoft.com/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0)
- [.NET 10 MAUI Windows unpackaged 发布](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli?view=net-maui-10.0)
- [Zulip Flutter outbox 状态机](https://github.com/zulip/zulip-flutter/blob/main/lib/model/message.dart#L940-L1014)
