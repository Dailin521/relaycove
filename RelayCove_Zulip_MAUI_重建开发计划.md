# RelayCove 重建计划：正式 Web + 原生 MAUI 双前端，Zulip 唯一后端

文档状态：Stage 21 外部门禁保留；Stage 22W Slice 1/2/3 已合并并部署；消息交互快速修订在本地分支实施；Stage 22M 待启动
目标版本：`2.0.0-alpha.1`
首发平台：Windows 11 x64
目标框架：`net10.0-windows10.0.19041.0`
最后更新：2026-08-13

## 1. 决策摘要

RelayCove 放弃自研聊天服务端、ASP.NET Core/SignalR 协议、旧 Shared DTO、Updater 与安装器，重建为直接连接 Zulip 的双前端客户端。Zulip Server 是用户、权限、频道、消息和实时事件的唯一事实源；RelayCove 只负责客户端体验、本地状态/缓存和平台集成。

2026-08-12 已确认真正双前端路线：

- 现有 Zulip 官方 Web 保留，不修改、不替换。
- `RelayCove.Web` 是可独立部署和正式使用的 Web 客户端，优先实现并完成浏览器验收。
- `RelayCove.App` 继续使用 .NET MAUI；在 Web 交互版本冻结后用原生 XAML/ViewModel 复刻，不使用 WebView。
- Web 与 MAUI 都直接连接同一个 Zulip Realm；不新增 RelayCove server、BFF、代理协议或第二套消息后端。
- 两端共享视觉 Token、交互规格、功能矩阵和验收场景，不共享 UI 运行时代码。
- Web 面向私域使用便利，默认“记住登录”，允许将 API Key 保存在当前浏览器本地；注销必须清除。

Stage 21 初始重建由用户明确要求直接在本地 `main` 实施。2026-08-12 用户另行授权提交、推送、合并和固定入口部署，形成 `main@53a4f1a`；2026-08-13 又明确授权当前消息交互分支使用指定账号做限定真实写验证。本轮授权不包含新的提交、推送、部署、标签或修改目标 Zulip 主机。旧 Git 历史和 `v1.0.0-rc.25` 标签保留为回滚点。删除使用普通 Git 变更，不使用 orphan、`reset --hard`、force-push。

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

目标主机的部署、Nginx、备份和恢复事实以 [主机配置索引](E:/GitHubProject/server-admin/servers/zulip-hklight/README.md) 及其链接文档为准。本项目不复制主机秘密；显式大版本部署只通过 host-key-pinned 的私有运维入口执行版本化静态发布，不在普通开发验证中修改主机。

### 2.2 未验证，禁止误报通过

| 门禁 | 状态 | 完成条件 |
|---|---|---|
| 干净 Windows 11 x64 VM 启动 | 未验证 | 在未安装 .NET 与 Windows App SDK Runtime 的 VM 中运行最终 ZIP |
| 真实 Realm Live 测试 | 未验证 | 两个专用账号 API key、隔离私有频道和显式写入授权齐全 |
| 人工密码登录 | 未验证 | 专用测试账号在最终 MAUI UI 中完成一次登录 |
| Stage 22W Web 验收 | Slice 1/2/3 已完成并部署；当前分支另完成 reaction/edit/delete/star、任意附件、导航折叠、退订、真实本地入口、86 单测、构建/Playwright 和限定 self-DM 写闭环；本轮 Full 已通过，记录时尚未提交/部署 | 真实已读/上传/退订仍需隔离目标；Stage 21/22M 门禁不受此结果替代 |
| Stage 22M MAUI 视觉验收 | 未开始 | Web 交互版本冻结后完成原生 Windows 真实窗口验收 |
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

### 3.2 当前共享消息能力明确不做

搜索、typing、presence、通知、push、SSO、多账号、AI、自动更新、安装器、MSIX、签名、Android、iOS、Mac Catalyst 和 Linux，以及频道订阅/创建/重命名/归档/成员管理。Stage 22W 已单独实现任意附件、当前用户退订、reaction、本人消息编辑/删除和收藏能力门，不表示 Stage 21/MAUI 已拥有或验收这些能力。

后续平台可以复用 Core、Zulip.Client 与 Data，但必须另行增加目标框架、图标、签名、后台生命周期和发布验证。本阶段不声称可交付这些平台；Linux 不支持。

### 3.3 Stage 22W 已实施范围

Slice 1 建立正式 Web 工程、组件化 UI 外壳以及登录/API 基础：

- TypeScript、React、Vite 与本地 bundle；生产运行时不依赖 CDN。
- 顶部产品栏、主导航、频道/私信分组、聊天骨架、多行 Composer、可折叠详情、设置、浅/深色和收窄布局。
- `GET /api/v1/server_settings`、`POST /api/v1/fetch_api_key`、邮箱 + API Key HTTP Basic 构造边界。
- Realm、邮箱和 API Key 浏览器恢复；默认记住登录；注销清除 local/session storage。
- 确定性开发 fixture 与正式 Zulip API/session 路径隔离，且 fixture 数据从生产构建排除。

Slice 2 在同一正式运行时补齐首个可用消息纵切片：

- `/users/me` 权威身份核对，`/register` 原子快照，`/events` 长轮询、退避、重启探测和坏队列重建；
- 已订阅频道/话题、1:1、群组和 self-DM、已知 Realm 联系人、新私信/频道话题入口；
- newest/older 每页 50 条 raw Markdown 历史、会话切换取消、服务器确认后标已读；
- 频道/DM 文本发送、`queue_id` + `local_id` 对账、500 ms Waiting、10 s WaitExpired、明确失败与正文恢复；
- message/edit/delete/move/flags、subscription/stream/user/restart 事件投影和撤权清理；
- 业务投影、queue、cursor 和 outbox 只保存在当前页面内存；不承诺刷新后离线历史。

Slice 3 完成首批完整交互能力，而不是只添加视觉按钮：

- 消息右键、`Shift+F10`/菜单键和触屏“更多”统一打开无 Realm 写入的操作菜单，支持回复草稿、复制正文/链接/ID和打开官方 Zulip；
- 用户/消息头像按同 Realm 白名单路径以受控 Blob 加载，失败回退首字母/Bot，不把 API Key 放入 URL 或 DOM；
- 同 Realm 图片 Markdown 通过 Zulip 临时授权 URL 读取，支持缩略图、下载、遮罩预览、三种关闭方式与焦点恢复；
- Composer 校验图片类型/大小，保留正文和分会话草稿，执行一次 upload + 一次 message POST；两阶段均不自动重试；
- 每消息最多 4 个预览，全局 4 个媒体并发、64 MiB Blob 预算；注销同步 abort 上传、释放 URL，并拒绝晚到结果继续发送。

2026-08-13 消息交互快速修订继续对齐 Zulip 12.1 官方 Web 源码：

- 当前用户发送的消息回声即使没有 `read` flag，也不进入本客户端未读计数；其他用户消息仍以服务端 flag 为准。
- 每条消息在悬停、键盘聚焦或触屏环境提供“引用、复制、更多”快捷操作；右键与键盘菜单继续共用完整动作集合。
- 引用草稿使用发送者标识、消息永久链接和 fenced `quote`，来源始终是完整 raw Markdown，因此正文、图片与其他附件链接一并保留。
- Composer 表情按钮提供本地 Unicode 表情选择器，按当前光标插入文本；每条消息另有真实 Zulip reaction 添加/移除入口。
- Web 已实现 reaction、本人消息编辑、本人消息永久删除和当前账号私有收藏；HTTP 成功后本地收敛，Zulip event/history 继续作为跨客户端权威状态。每消息写入串行，网络/超时结果未知时不自动重试。
- 日常 `npm run dev` 与双击入口默认打开正式登录并直连真实 Realm；fixture 只保留给确定性自动化，不再作为日常人工开发入口。
- self-DM 始终从已认证 current user ID 生成稳定入口；频道和私信分组可独立折叠并保存为非敏感浏览器偏好。
- Composer 支持多选或拖放最多 10 个任意附件；只有安全栅格图片生成本地缩略图，其他文件显示文件卡片。附件按顺序各上传一次，再用经过转义的服务端 `filename/url` 组成唯一一次消息 POST。
- 同 Realm 非图片上传使用受控下载卡片：先以 Basic 换取临时 URL，再用无 Authorization、无 referrer 的请求读取有大小上限的 Blob；不内嵌 SVG/HTML/PDF/Office 等主动内容。
- 频道详情支持当前用户按真实订阅名调用 `DELETE /users/me/subscriptions` 退订；确认成功或已退订都复用既有 `subscriptionRemoved` 清理，结果未知不自动重试。

所有仓库自动门禁继续只使用 mock/fake HTTP。2026-08-13 在用户明确授权后，使用用户指定成员账号对发给自己的临时私信执行发送、reaction、收藏、编辑、事件 flags 核对和删除闭环，测试消息均已删除；没有操作其他用户消息、频道、上传或标记已读。本地 Chromium 另验证正式登录、真实会话和消息读取，控制台无错误。任意附件与退订已通过 fake HTTP/浏览器链路验证；真实上传和真实频道退订没有执行。全局/服务端搜索、mention 候选、saved 列表页、presence、通知和其余频道管理仍是独立能力门。

## 4. 架构和依赖边界

```text
RelayCove.Web ───────────────────────────────> Zulip Realm
  TypeScript/React/Vite + browser HTTP/session

RelayCove.App
  ├─> RelayCove.Core
  ├─> RelayCove.Zulip.Client ─> RelayCove.Core
  └─> RelayCove.Data         ─> RelayCove.Core
```

| 工程 | 责任 | 禁止事项 |
|---|---|---|
| `RelayCove.Web` | 正式 React UI、浏览器 Zulip HTTP/session、内存 reducer/store/outbox、Web 交互状态 | 不引用 MAUI UI runtime、不导入生产 fixture、不新增 server/BFF/proxy、不把业务投影伪装成持久离线缓存 |
| `RelayCove.App` | MAUI XAML、ViewModel、Windows composition root、SecureStorage、Preferences | 不直接使用 HttpClient/SQLiteConnection/Zulip DTO |
| `RelayCove.Core` | 领域模型、公共契约、单通道 reducer、会话/同步用例 | 不引用 MAUI、SQLite、JSON DTO |
| `RelayCove.Zulip.Client` | 安全 HttpClient、Basic Auth、REST/事件队列、DTO 映射 | 不保存凭据、不操作数据库、不暴露 JSON 给 UI |
| `RelayCove.Data` | 每账号 SQLite、迁移、事务、mutation lane | 不存 API key、queue_id、last_event_id 或 outbox |

MAUI UI 只依赖 `IClientSession`。所有 register、历史、实时事件、发送对账和 SQLite 写入通过单一 mutation lane；UI 线程不执行数据库 I/O。Web 使用独立的 TypeScript API/session、纯 reducer/store 和 React 投影，不复用 .NET UI runtime；两端只共享书面合同和验收输入。Web 当前不引入 SQLite/IndexedDB/Service Worker，持久化仅限经确认的凭据与非敏感外观偏好。

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

### 6.3 Web 浏览器凭据

- Realm 只接受无 userinfo、path、query、fragment 的规范 HTTPS origin。
- `/server_settings` 无凭据；密码只进入 `/fetch_api_key` 的 form body，成功后不持久化密码。
- 默认勾选“记住登录”：Realm、邮箱、API Key 写入当前 origin 的 local storage；取消勾选时只写 session storage。
- 注销同时清除 local/session storage；损坏或不完整的 envelope 删除并要求重新登录。
- API Key 不进入 URL、UI、日志、异常、测试快照或构建产物。浏览器本地保存 Key 是已确认的私域便利取舍，不宣称等同 MAUI SecureStorage。
- 正式部署必须验证目标 Realm 的浏览器同源/CORS 策略和静态托管安全响应头；当前选择同源 `/relaycove-web/` 静态入口并已验证。若未来更换 origin，CORS 门禁重新打开；不得为绕过浏览器限制新增 RelayCove proxy/BFF。

### 6.4 网络安全

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

## 9. 双前端 UI 路线

Stage 21 的 MAUI Shell 已冻结功能但尚未完成最终视觉：

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

用户已于 2026-08-12 确认并冻结 `docs/ui/baselines/chat-ui-v1/`。该目录继续保持不可变，只作为历史视觉基线和哈希证据，不再作为日常 Web 运行/验收入口。正式 RelayCove.Web 是后续 Web 交互事实基准；Stage 22M 使用原生 XAML/ViewModel 复刻，不得以 WebView 承载 Web。任意附件、当前用户退订、reaction、本人消息编辑/删除和收藏已由 Web 独立实现；搜索、`@` 候选及其余频道管理仍需独立能力门。

## 10. 实施切片与完成定义

### Stage 22W：RelayCove Web 正式客户端

1. **Slice 1 — 生产基础与原生组件化**：工程/锁文件、Token、UI 外壳、fixture 隔离、登录/API/session 边界、typecheck/unit/build/Playwright、固定视口截图。
2. **Slice 2 — 正式消息纵切片**：实现 users/me、register、events、topics、历史、正式频道/DM/联系人投影、权威未读/标已读、文本发送/outbox、重连和坏队列重建；生产路径保持与 fixture 隔离。
3. **Slice 3 — 消息操作、头像与图片**：实现完整菜单键盘/触屏入口、同 Realm 头像 Blob、受控图片读取/预览/下载、Composer 图片上传发送、资源预算和注销竞态保护；自动化只使用 fake HTTP。
4. **消息交互正式写入**：修复本人消息误计未读，完善快捷工具、完整 raw Markdown 引用和 Composer Unicode 表情，并实现 reaction、本人消息编辑/删除、当前账号收藏及相应 realtime/history 收敛。
5. **附件与导航写入跟进**：增加稳定 self-DM、频道/私信分组折叠、任意多附件选择/拖放/安全下载，以及当前用户频道退订；生产仍直连同一 Zulip Realm。

截至 2026-08-12，Slice 1/2/3 已通过 Fast/Full 与独立只读复核，提交 `53a4f1a` 已合并至 `main`，并以版本化原子切换同步至固定 `/relaycove-web/` 入口。2026-08-13 本地跟进已完成 typecheck、86/86 单测、production build、Playwright 6/6 fixture/formal 场景与 1/1 固定部署路径场景、真实浏览器读取及限定自发私信写入/flag 闭环。后续对同一代码树运行的仓库级 Full 亦已通过：.NET Debug/Release 135/135、Web 86/86、Playwright 6/6 + 1/1、Windows 包生成且零构建警告/错误。记录该证据时尚未提交、推送或部署；真实附件上传、退订和标记已读仍未执行。

开发节奏固定为：日常双击 `start-web-dev.cmd` 在本机启动 `npm run dev` 并打开正式登录入口 `http://127.0.0.1:5173/`，直接使用真实 Realm 数据；fixture 仅由 `--mode fixture`/E2E 显式启用。只有需要大版本人工验收时才双击 `deploy-web.cmd`，执行完整 Web 门禁、版本化上传、SHA-256 校验和原子 `current` 切换，并打开固定 `https://hklight.2000521.xyz/relaycove-web/`。不启用 deploy-on-save；官方 Zulip `/` 与旧 `/relaycove/` 均保持原路由。
3. **交互冻结**：每个已验收版本记录 Token、规格、功能矩阵、场景、截图与差异，作为 22M 输入。

### Stage 22M：MAUI 原生视觉与交互对齐

Web 对应交互版本冻结后，按 Token/规格/功能矩阵/场景用原生 MAUI 复刻。不得共享 React 运行时代码或使用 WebView；MAUI 继续通过 `IClientSession` 使用 Zulip 权威状态。Web 浏览器验收不能替代 Windows 真实窗口、200% 缩放、人工登录或干净 VM 验收。

### Stage 21 历史实施切片

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

每个切片只有在代码、测试、文档、独立复核和限制说明齐全时完成。两端状态分别报告，不能用一端的通过替代另一端或 Stage 21 外部门禁。真实写入、推送、合并、标签和发布按当轮明确授权执行。

## 11. 自动化验证

### 11.1 Fast

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

执行 .NET 格式/静态检查、Debug build、Core/Zulip/Data/App 单元测试，以及 Web typecheck、unit 和 production build。NuGet assets、npm 依赖和 Chromium 必须在单独 bootstrap 中显式预置；Fast 只走 `--no-restore` .NET 命令。Web 构建检查无运行时 CDN且不含开发 fixture。Fast 不得恢复/安装依赖、下载浏览器、连接外部网络、使用真实凭据或运行 LiveTests。

### 11.2 Full

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

执行 Fast、Release build、本地全部 .NET 测试、仅 MAUI app 项目 publish、ZIP 内容检查、icon/native runtime 检查和秘密扫描，并在 E2E 专用本地构建上运行 Web Playwright。Playwright 只访问 `127.0.0.1` 和被拦截的 fake HTTP；禁止对 solution 执行 `dotnet publish`。

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
| Web API/session | Realm origin、无凭据 probe、密码只进 form、users/me 身份核对、Basic Auth、register/events/topics/history narrow、read flag、send form、queue cleanup/rebuild、固定脱敏错误、remember local/session、损坏恢复、logout 双清除 |
| Web state | register 原子替换、同事件 patch 组、撤权清理、选会话取消、50 条分页、权威 unread、local echo、500 ms/10 s outbox、模糊发送零自动重试、401 清凭据 |
| Web UI | 正式频道话题、1:1/group/self-DM、联系人、新会话、raw Markdown、Composer 发送/恢复、连接状态、设置；浅/深主题、1440×900、1024×768、低于 720 单栏、键盘焦点、Composer clamp、详情 Escape、console 0 error/warning |
| Web build | 锁定依赖、生产 bundle、无 runtime CDN、开发 fixture 从 production 排除、E2E 只用 fake HTTP |
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
| Web local storage API key 被 XSS/本机访问 | 高 | 已确认私域便利取舍、严格 CSP/依赖锁、最小渲染面、默认记住可取消、logout 双清除、Key 不进 URL/日志/UI/快照 |
| 独立 Web origin 受 Zulip CORS 限制 | 高 | 当前固定同源子路径已验证；若更换 origin，重新执行 CORS 门禁；不新增 proxy/BFF，不把 mock 结果误报为部署通过 |
| Web/MAUI 交互漂移 | 中高 | Web 先验收并冻结版本；共享 Token/规格/矩阵/场景，分别跑浏览器与 Windows 门禁，不共享 UI runtime |
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

Stage 22W 每个 Slice 只有在 production build、typecheck、unit、Playwright、固定视口截图、fixture 排除以及对应认证/协议/同步/outbox 独立复核全部通过时才能标记完成。Slice 2 的 fake-HTTP 证据证明正式代码路径和协议编排；后续目标 Realm 的窄认证/register 检查只证明该边界，不证明完整浏览器读写。任何这些证据都不能代替 Stage 22M 或 Stage 21 外部门禁。

Stage 22W Slice 1/2/3 已由提交 `53a4f1a` 合并并推送至 `main`。2026-08-13 消息交互修订在 `codex/stage-22w-message-interactions` 本地分支实施；用户已明确授权本轮真实账号消息写验证，实际范围严格限制为自发临时私信且已删除。当前没有提交、推送、部署或标签授权；既有固定 `/relaycove-web/` 部署、Zulip 官方 Web 与旧服务保持不变。删除旧 `%LOCALAPPDATA%\RelayCove` 等动作仍需单独确认精确目标。

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
