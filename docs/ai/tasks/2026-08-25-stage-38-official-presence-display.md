# Stage 38：Zulip 官方在线状态与个人状态显示

Status: implementation and deterministic verification complete; awaiting user Visual Studio confirmation

- Created: 2026-08-25
- Product scope: `RelayCove.App` MAUI Windows only
- Branch: `main`
- Baseline: `main@b0fb6ee`

## 1. 用户问题

RelayCove 当前不读取或显示 Zulip 的官方 presence。用户先要求接入在线、离线和忙碌状态，随后补充要求接入 Zulip 官方预制图标的个人状态。

## 2. 协议结论

- Zulip 的权威 availability 状态是 `active`、`idle`、`offline`，没有独立的 `busy` presence 枚举。
- 产品按用户指定文案显示为：`active` = “在线”、`idle` = “忙碌”、`offline` = “离线”；Core 与协议层仍保留 `idle` 原始语义。
- register 初始快照请求 `presence`，并使用 numeric user ID 的 modern presence 格式。
- 事件队列订阅 `presence`，支持一个事件同时更新多名用户。
- presence 事件只负责用户重新上线的及时更新；客户端仍每 60 秒调用一次官方只读 `GET /realm/presence` 刷新全量可见状态。
- 当前用户可在左上头像账户菜单选择“在线 / 忙碌 / 离线”。在线与忙碌分别通过 `POST /users/me/presence` 上报 `active` / `idle`；离线使用官方隐身设置 `PATCH /settings presence_enabled=false`，因为 presence 上报接口不接受 `offline`。
- Realm 明确返回当前用户 `presence_enabled` 后才开放设置；缺失时失败关闭。从隐身恢复固定先上报目标 active/idle，再将 `presence_enabled=true`，避免解除隐身后短暂暴露旧状态。
- 在线/忙碌选择只代表 RelayCove 客户端的设备上报；其他已登录客户端仍参与 Zulip 聚合。应用启动且未隐身时默认上报在线；手动忙碌在当前运行期保持，离线隐身是服务端账号设置。
- presence 不写入 SQLite；重启后的旧缓存不得被当作当前在线状态。读取失败保留当前进程内最后一次快照，并依据官方 200 秒默认离线阈值自然降为离线。
- Realm 禁用 presence、register 字段缺失或尚未取得权威快照时隐藏状态，不推测在线。
- Zulip `user_status` 是独立于 availability/presence 的个人状态，由可选状态文字与 emoji 组成，不能把它合并成在线状态枚举。界面可同时显示“在线 · 📅 会议中”。
- register 的 `user_status` 是按 user ID 索引的初始快照，事件队列的 `user_status` 事件负责实时增加、修改和清除；两者都只保存在当前 session 内，不写 SQLite。
- 读取端接受服务端任意合法状态文字及 `unicode_emoji`、`realm_emoji`、`zulip_extra_emoji` 身份；写入界面本轮只提供 Zulip 12.1 官方七个预置项：🛠️ 忙碌、📅 会议中、🚌 通勤中、🤕 病假、🌴 休假、🏠 远程办公、🏢 在办公室，以及清除状态。
- `POST /users/me/status` 是部分更新接口，因此客户端每次设置都显式提交 `status_text`、`emoji_name`、`emoji_code`、`reaction_type` 四个字段；清除时四字段全部为空，避免遗留旧文字或 emoji。写入不自动重试，账号/运行代次变化后丢弃晚到结果，模糊结果显示未确认。

官方依据：

- `GET /realm/presence`：读取组织内可访问用户的 presence。
- `POST /register`：`presence` fetch event type 与 `slim_presence=true` 的 modern 初始快照。
- `presence` event：重新上线的即时事件；官方明确要求同时轮询主 presence endpoint。
- Zulip 默认：60 秒 presence 刷新间隔、200 秒离线阈值。
- `POST /register`：`user_status` 初始快照。
- `user_status` event：个人状态的实时变更。
- `POST /users/me/status`：设置或清除当前用户的状态文字与 emoji。
- Zulip 12.1 `user_status_ui.ts`：七个官方预置状态及固定 Unicode emoji 身份。

## 3. UI 范围

- 微信式统一左栏的一对一私信头像右下角显示状态点：在线为绿色、忙碌为橙色、离线为灰色。
- 一对一私信聊天头部副标题显示“在线 / 忙碌 / 离线”。
- self-DM 不显示 presence；私有群聊不聚合成员状态。
- 尚无权威状态时不显示状态点，聊天头部仍显示“私信”。
- 点击左上头像后，账户菜单显示当前状态和“在线 / 忙碌 / 离线”三个选项；离线选项的辅助说明明确其为对他人显示离线的隐身模式。
- 左上标题栏的本人头像和账户菜单内的本人头像都显示同一权威状态点；状态已知但设置权限未知时仍可显示，只有状态本身未知时隐藏。
- 一对一会话行在姓名旁显示对方个人状态 emoji，悬浮可查看状态文字；聊天头副标题同时显示 availability 与个人状态。self-DM 和群聊不投影单个成员个人状态。
- 账户菜单把“在线状态”和“个人状态”分开。个人状态区显示当前权威值、七个官方图标预置项和清除按钮；设置期间只显示进度，不乐观改写头像或会话状态。

## 4. 实现与测试日志

### 4.1 现象与根因

- App 只有用户资料与头像投影，register/event types 没有请求 `presence`，Core 也没有 presence 状态模型。
- 只在登录时读取一次会长期过期；官方明确说明 presence event 主要及时通知“重新上线”，已在线用户的时间戳刷新仍需轮询主 presence endpoint。

### 4.2 被否决方案

- 不从消息发送时间、窗口焦点、最近聊天或本机活动推测他人在线状态。
- 不把 presence 写入 SQLite；跨重启展示旧在线状态不可靠。
- 不把 Zulip `user_status` 或已弃用的 `away` 当成 availability 的“忙碌”。availability 的“忙碌”仍只表示 `idle`；同名的 🛠️“忙碌”是用户主动设置的独立个人状态。
- 不向官方接口发送 `offline`，因为该接口只接受 `active` / `idle`；离线严格映射为官方 `presence_enabled=false` 隐身设置。
- 不把一次手动选择当作可无限重试的普通写入；手动切换只执行一次固定序列，后续 active/idle 请求是官方客户端协议要求的周期心跳，不重放失败的 UI 操作。

### 4.3 最终实现

- Core 新增 modern active/idle timestamps、默认 200 秒离线解析、session-only presence 快照与实时事件 reducer。
- Zulip.Client register 请求 `presence`、`slim_presence=true` 和 `simplified_presence_events`，解析 modern register/event 格式；每分钟只读 GET 使用 legacy aggregated 结果并规范化回同一 timestamp 模型。
- ClientSession 仅在 Realm 明确允许 presence 时刷新；401 沿用注销安全路径，网络/限流/协议失败保留旧快照，账号变化拒绝晚到结果。
- register 只有在 Realm 明确启用且返回结构正确的 presence 快照时才开放显示；缺失或畸形快照失败关闭。presence 事件不能把禁用/未知状态重新启用。
- 60 秒全量读取与 active/idle 心跳使用独立、可取消的 session 循环，不等待消息长轮询返回；停止、注销或账号切换会同时取消事件和 presence 循环。手动状态写入与周期心跳共用串行通道。
- Zulip.Client 增加官方 ping-only presence 上报和 `presence_enabled` 设置；register 从当前用户对象读取权威隐身设置，字段缺失时隐藏入口。
- App 一对一左栏头像显示绿色/橙色/灰色状态点，聊天头显示在线/忙碌/离线；self-DM、群聊和未知状态保持无状态点。
- App 左上账户菜单增加当前状态和三个设置按钮；忙碌使用 `idle`，离线使用隐身模式。明确拒绝保留原状态并显示错误；网络、超时、5xx 或异常 2xx 不重发原请求，而是显示“状态结果未确认”，允许用户稍后重新选择。
- 周期读取与心跳捕获账号、运行代次和凭据；晚到的成功结果或 401 只能作用于发起它的当前会话，账号/运行期已经变化时静默丢弃。
- 首次 Visual Studio 检查发现账户菜单没有状态入口。根因是 Zulip 12.1 将当前用户的 `presence_enabled` 放在 register 的 `user_settings` 对象中，而客户端既未请求 `user_settings`，又只从 `realm_users` 成员条目读取该值。最终请求并解析官方 `user_settings.presence_enabled`，同时保留旧服务器字段作为降级读取；未知或畸形对象仍失败关闭。
- `user_settings/property=presence_enabled` 实时事件同步当前菜单；在官方客户端切换隐身后无需重启 RelayCove。缺失或错误类型的该设置事件将入口失败关闭，不保留可能过期的确定值。
- 本人头像复用当前用户的权威 `OwnPresenceStatus`：标题栏与账户菜单头像右下角均显示绿色/橙色/灰色状态点。状态显示不依赖设置入口是否可用，避免只有可写时才展示已知状态。
- 用户检查发现账户菜单显示绿色“在线”时，标题栏本人头像仍是灰点。两处状态值一致，差异来自 `TitleBar.LeadingContent` 内的颜色 `DataTrigger` 未可靠刷新；没有引入第二份状态或定时同步。最终由 ViewModel 提供唯一 `OwnPresenceBrush`，两处直接绑定它，并在 `ProductBarView.Bind` 对标题栏状态点显式绑定同一 ViewModel 源。
- 后续检查反馈状态设置“卡”。原因并非同步网络 I/O 阻塞 UI，而是当前状态按钮仍允许重复提交，每次都等待一次无意义的服务器写入；有效切换期间又只有按钮禁用，没有即时反馈。最终当前状态按钮不可重复点击，命令层也拒绝相同状态写入；有效切换立即显示“正在切换为…”和进度指示，头像继续保持上一次权威状态，服务器确认后才改变。忙碌状态变化只通知相关可用性/进度属性，状态画刷改为复用三个固定实例，避免请求开始时无意义重绘头像。
- 用户随后指出官方还有预制图标状态。最终新增独立 `UserStatusState`、register 快照、实时事件和当前用户写通道；它不复用 presence、不会把自定义状态伪造成在线状态，也不持久化到 SQLite。
- App 账户菜单提供 Zulip 12.1 的七个官方预置图标和清除；一对一会话行显示 emoji，聊天头组合显示例如“在线 · 📅 会议中”。读取端保留任意服务器状态，预置列表只约束本轮写入入口。
- 个人状态命令与 availability 心跳使用不同串行通道。写请求固定发送完整四元组且不重试；晚到结果只能作用于发起它的当前账号和运行代次。预置对象从 MAUI 画刷静态初始化中隔离，避免仅点击个人状态命令时触发无关原生控件初始化。

### 4.4 当前验证

- 最新当前树：Core 166/166、Zulip.Client 135/135、Data 35/35、App 321/321 通过；App 使用 `.verify/stage38-user-status/` 隔离输出并完成 Debug 依赖构建，0 error。
- 较早的 presence-only `pwsh ./scripts/verify.ps1 -Mode Fast`：构建 0 warning/0 error；Core 159/159、Zulip.Client 123/123、Data 34/34、App 316/316 通过；该结果不声称覆盖后续个人状态代码。
- 首轮独立只读复核发现缺失快照误判离线、禁用状态被事件重新启用、轮询受长轮询延迟三个问题；二次复核确认后两项关闭，并发现对象内部畸形条目仍可能开放快照。最终实现对非法用户 ID、非对象条目及缺失/错误时间戳整份失败关闭，并增加回归测试；最终定向复核确认无剩余 P0/P1/P2。
- 当前用户写路径复核随后发现周期请求晚到结果/401 与模糊写结果仍可能污染界面；实现账号、运行代次和凭据三重当前性校验，并将模糊写结果投影为未确认后，最终独立只读复核确认无剩余 P0/P1/P2。
- 用户 Visual Studio 人工确认尚待完成。
- 本人头像状态点的默认输出定向测试尝试被用户正在运行的 Visual Studio 进程锁定 `RelayCove.App.dll`；未关闭用户窗口。随后改用 `.verify/stage38-own-avatar/` 隔离输出，相关 XAML/ViewModel 定向测试 16/16 通过并完成 App 依赖构建，`git diff --check` 通过。此前 Fast 结果不误记为覆盖该视觉补丁；最新视觉结果等待用户在 Visual Studio 重新启动后确认。
- 状态点不一致修复使用 `.verify/stage38-own-avatar-consistency/` 隔离输出，相关 XAML/ViewModel 定向测试再次 16/16 通过并完成 App 依赖构建；用户 Visual Studio 复验尚待完成。
- 状态设置交互修复使用 `.verify/stage38-own-presence-ux/` 隔离输出，相关 XAML/ViewModel 定向测试 18/18 通过并完成 App 依赖构建；覆盖相同状态不重复写、服务器确认期间即时进度以及三种按钮可用性。用户 Visual Studio 复验尚待完成。
- 独立只读复核确认个人状态必须与 presence 分离、读取任意服务端状态、写入完整四字段、独立串行且不重试、禁止 SQLite 持久化；首轮最终 diff 复核另发现异常 2xx 会误确认、无关本人事件会错误确认模糊写，以及畸形 emoji 三元组可能被误当清除。最终实现要求 `result=success` 且没有忽略参数，只接受写入起始游标之后且目标四元组匹配的本人事件作为确认，并对部分/未知 emoji 三元组失败关闭；均已增加回归测试。最终只读复核确认无剩余 P0/P1。
- 未运行 Full、Live、打包、Agent 启动应用、截图、认证 Realm 访问或 Agent 外部写入；真实状态切换只会在用户运行客户端并点击时发生。

## 5. 验证边界

- Agent 运行 Core、Zulip.Client、App 定向测试与 App Debug 构建。
- 不运行 Full、Live、打包或真实 Realm 写入。
- 状态点、颜色和聊天头部呈现由用户通过 Visual Studio 快速人工确认。
- 用户确认前不提交、不推送。
