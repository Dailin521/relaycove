# Stage 22 — Native Chat UI

- Status: planned
- Design baseline: `docs/ui/baselines/chat-ui-v1/`
- Interaction source: `docs/ui/INTERACTION_SPEC.md`
- Execution branch: not created

## Objective

把用户确认的 `chat-ui-v1` 视觉和交互结构转换为 Windows-first 原生 .NET MAUI 界面，同时继续通过 `IClientSession` 使用 Zulip 权威状态。禁止把冻结 HTML 放入 WebView，禁止为了匹配原型重新引入服务端、私有协议或第二套业务状态。

Stage 22 目前只登记为 planned。Stage 21 的 Live、人工密码登录和干净 Windows 11 VM 门禁仍未完成；本任务不能被用来把 Stage 21 标记为完成。

## Activation gate

开始改 MAUI 前必须满足：

- 用户确认 `chat-ui-v1` 作为首个原生转换基线；
- 交互规格中的范围归属和已知差异无 P0/P1 歧义；
- 确认首个交付只做原生视觉迁移，还是同时启用某个新能力门；
- 创建 `codex/` 前缀执行分支，除非用户另有明确指示；
- 记录变更前 `git status` 和 Stage 21 外部门禁状态。

## Scope split

### 22A — App-only 原生外壳

不新增网络、存储或领域契约：

- Windows 标题栏与置顶、最小化、最大化/还原、关闭。
- 主导航、频道与 1:1/群组/self-DM 分组、聊天头部、消息列表、底部输入区、详情和设置视图。
- 浅色/深色 token、密度、字号、圆角和 1440×900/1024×768 布局。
- 将 `MainPage.xaml` 拆为可测试的私有 App 组件。
- 继续显示 raw Markdown，不引入 WebView 或 HTML 渲染。
- 详情和导航只显示当前契约能证明的数据；频道成员/人数、共同频道、presence、saved flags 和 capability 在能力门前隐藏或标为不可用。

### 22B — 现有 Stage 21 状态保真

- 把 Connected、Offline、Reconnecting、RateLimited、Locked、ReauthRequired 映射到明确 UI。
- 把 Hidden、Waiting、WaitExpired、Failed 映射到消息气泡和恢复入口。
- 从 Core `UnreadState` 实时投影会话红点和主导航总数。
- 使用 keyed reconcile 保持选择、滚动、草稿和焦点。
- 将单行 `Entry` 替换为多行 `Editor`；原生拖柄范围 72–300 px，键盘步进 16 px。
- 只保留 Stage 21 可执行的文本发送；上传按钮在附件能力门前必须禁用或明确标为后续。
- 完整保留 Stage 21 的 1:1、群组和 self-DM 导航、分页、未读、草稿、发送与 outbox；群组收件人来自 `DirectMessage` 规范 ID 集合。

### 22C+ — 独立能力门

下列能力逐项实施，不能合并成一个无边界“大 UI”提交：

1. 频道成员关系、共同频道、presence 与已保存消息读取。
2. 全局搜索。
3. 图片上传、预览和授权下载。
4. 频道 `@` 成员候选。
5. 频道重命名、归档/删除语义、成员移除和主动退出。
6. 反应与其他富消息能力。

每项在开始前补充官方 Zulip 12.1/OpenAPI 证据、契约、数据迁移影响、安全审查范围和确定性测试。

## Concrete implementation order

### Slice 1 — Token 和窗口骨架

- 新建 App 内部颜色、间距、字号、圆角和尺寸 token。
- 实现 Windows 标题栏 adapter 和原生窗口按钮。
- 建立四列布局及收窄 VisualState。
- 验收：1440×900 与 1024×768 浅/深色截图，无横向滚动。

### Slice 2 — 导航与会话投影

- 拆分 `NavigationRailView` 与 `ConversationPaneView`。
- 新建纯 `ShellStateProjector`，从 `ClientState` 生成 keyed UI 项。
- 保留 `ChannelTopic` 和 `DirectMessage` 的真实会话键，并覆盖 1:1、群组和 self-DM 的稳定标题/选择。
- 通过未读事件增减、read flag、消息移动/删除和撤权回归测试。
- 验收：未读变化不清空列表、不丢失选中会话。

### Slice 3 — 消息列表与状态

- 拆分 `ChatHeaderView` 和虚拟化 `MessageListView`。
- 实现正常、加载、空、离线、锁定和 outbox 状态模板。
- 保持分页锚点，切换会话取消旧请求。
- 验收：频道、1:1、群组、self-DM 的 50 条分页、长文本、连续快速切换和失败恢复。

### Slice 4 — Composer 和草稿

- 新建 `ComposerView`，使用多行 `Editor`。
- App 层保存每会话草稿；View/Behavior 管理光标、拖拽和高度 clamp。
- 离线时保留草稿，不触发发送；模糊失败不自动重试。
- 群组 DM 必须从选中 `DirectMessage` 的规范参与者集合发送，不能解析显示标题。
- 用提交 token/草稿版本隔离发送快照；成功确认不能清除发送后的新输入，也不能在会话切换后恢复已发送正文或图片。
- 验收：1:1/群组/self-DM 草稿隔离、发送中切换的 success/WaitExpired/Failed（正文与图片）、指针、键盘、窗口收窄、焦点恢复和 Ctrl+Enter。

### Slice 5 — 详情与设置

- 新建 `DetailsPaneView` 和原生设置页。
- 只显示当前已有数据能诚实支撑的频道/话题、DM 参与者、缓存、账户和外观信息；成员关系、共同频道、presence、saved flags 和 capability 在能力门前隐藏或标为不可用。
- 清缓存与注销继续复用现有安全命令和确认状态。
- 新能力入口按 capability/feature gate 禁用或隐藏。

### Slice 6 — Windows UI 验收

- 1440×900、1024×768，浅色、深色，100% 和 200% 缩放。
- 鼠标、全键盘、滚轮、拖拽、窗口最大化/还原。
- 长列表、空会话、离线、Waiting、WaitExpired、Failed、Locked。
- Fast、Full 与真实窗口证据分别记录。

## Proposed code landing points

| 责任 | 目标位置 |
|---|---|
| App views | `src/RelayCove.App/Views/` |
| App components | `src/RelayCove.App/Controls/` |
| UI state/projectors | `src/RelayCove.App/ViewModels/` |
| Windows window behavior | `src/RelayCove.App/Platforms/Windows/` |
| Theme/tokens | `src/RelayCove.App/Resources/Styles/` |
| App regression tests | `tests/RelayCove.App.Tests/` |

不得因为目录规划而新增不需要的公共 API；每个切片只创建当下被使用的类型。

## New capability gates

### Membership and saved-data gate

- 分开建模 Realm 用户、频道成员关系、共同频道、presence 和频道 capability，禁止从 Realm 用户列表推断频道成员。
- 为 saved/starred flags 增加协议、Core 投影、账号隔离缓存和撤权清理测试；启用前不声称“已保存”可用。
- 只读成员能力不授权频道管理写入；管理命令仍需独立 gate 和服务端复核。

### Search gate

- Core：`SearchQuery`、`SearchResult`、来源和取消语义。
- Data：缓存搜索、特殊字符和账号隔离。
- Zulip.Client：在线 narrow/search。
- App：分组、部分/单数字匹配、绿色 spans、上下键和过期结果抑制。

### Attachment gate

- 上传、授权下载和重定向/脱敏安全复核。
- 图片选择取消、类型/大小、预览移除和失败恢复测试。
- 消息 POST 仍只发送一次；模糊结果不自动重发。

### Mention gate

- 决定候选是频道成员还是 Realm 用户，并取得可靠数据。
- 频道可用，任何 DM 不弹候选。
- 解析光标、重名用户和 Zulip Markdown 格式测试。

### Channel management gate

- 建模服务器 capability，不只使用 `IsAdmin`。
- 重命名、归档/删除、移出成员和退出分别确认 API 语义。
- 403/权限变化刷新、危险确认和外部写入授权。

## Validation

开发中：

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

交付提交前：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

任何协议、同步、数据、outbox 或打包变更必须独立只读复核。真实 Zulip 写入、Live、推送、标签和发布仍按当次授权执行。

## Completion criteria

- 22A/22B 原生 UI 与冻结规格一致，差异有用户确认记录。
- 不使用 WebView，不新增 RelayCove server，不越过四层依赖边界。
- App 测试覆盖投影、未读、选择保持、composer clamp 和状态模板。
- App/Session 测试覆盖 1:1、群组和 self-DM 的稳定会话键、标题、草稿、收件人和 outbox。
- Fast/Full 在交付提交上通过。
- Windows 真实窗口完成目标视口、主题、键盘、缩放和长列表验收。
- 未启用的成员/已保存读取、搜索、附件、mention、频道管理和反应保持明确 capability gate。
- Stage 21 的 Live、人工登录和干净 VM 状态继续按各自证据报告。
