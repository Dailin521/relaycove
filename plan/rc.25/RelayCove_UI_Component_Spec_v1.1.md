# RelayCove UI Component Specification v1.1（可执行规格）

> 本文件取代 v1.0 作为 rc.25 的组件实施依据；v1.0 保留不改。视觉与布局固定值见 [`RelayCove_UI_Redesign_Implementation_Spec_v1.1.md`](RelayCove_UI_Redesign_Implementation_Spec_v1.1.md)。

## 1. 通用边界

- 组件只承载展示、可用性和用户意图；通过 DependencyProperty、DataBinding 与 RoutedEvent 输出意图。
- 禁止组件持有 HTTP、SignalR、SQLite、附件服务、更新协调器或账户 runtime；`MainWindow` 保留异步、取消、Dispatcher、generation 和选择租约。
- 所有焦点状态可见，所有仅图标按钮具有 `AutomationProperties.Name`；未开放入口可点击且只请求本地非模态提示。
- 使用冻结的 `ClientTheme`、`ClientIcons`、`ClientControls` 资源。禁止图片模拟控件、重复硬编码 token、新字体或 UI 框架。

## 2. 展示组件

| 组件 | 输入与职责 | 输出意图 | 必须保持 |
| --- | --- | --- | --- |
| `TitleBarControl` | 标题、窗口状态、命令可用性 | 最小化、最大化/还原、关闭 | `WindowChrome`、拖动、双击、Alt+Space；关闭只走 `Window.Close()` |
| `NavigationRailControl` | 当前导航、账户摘要、可用性 | 导航、设置、未开放功能 | 72/64px 断点；聊天=All、频道=Channels |
| `ConversationPanelControl` | 会话、分组、筛选、搜索、选择 | 筛选、选择、创建频道 | 本地 `OrdinalIgnoreCase` Name/Preview 搜索、虚拟化、选择不被过滤清除 |
| `ChatHeaderControl` | 真实标题、描述、成员数 | 成员、搜索、未开放功能 | <1400px 成员入口只反馈提示 |
| `MessageListControl` | 权威消息与加载状态 | 回复、复制、条件式重试、未开放功能 | 无气泡流、日期/新消息线、链接、提及、图片/文件安全路径 |
| `ComposerControl` | 正文、回复、候选、附件、发送状态 | `@`、附件、发送、未开放功能 | 多行、Enter 发送、Ctrl+Enter 换行、拖入/粘贴与成功清理语义 |
| `SettingsPanelControl` | 显示名、连接、服务器、通知、未读、更新 | 重连、检查更新、退出账户 | 既有设置与生命周期路径 |
| `UiNoticeHost` | 单条短提示与计时 | 无 | 约三秒、连续点击重置、非模态、无业务副作用 |

## 3. 状态与功能契约

`ClientUiFeatureId`、`ClientUiFeatureAvailability`、`ClientUiFeatureDescriptor`、`ClientNavigationSection` 与 `ClientConversationFilter` 都是 Client 内部展示类型，不得放入 Shared。RoutedEvent 至少包括 `NavigationRequested`、`ConversationFilterChanged`、`ConversationSelectionRequested`、`MembersRequested`、`SearchRequested`、`SendRequested`、`UnavailableFeatureRequested`。

可用功能：账户/设置、聊天、频道过滤、成员抽屉、显式消息搜索、回复、复制、条件式重试、附件、`@`、正文与发送。未开放功能：联系人、通知中心、文件中心、更多、置顶、会话通知、Emoji、语音、主动截图、发送下拉、反应、转发、收藏、消息置顶和删除。

未开放功能不得设置 `IsEnabled=false`；必须有 ToolTip、自动化名称和无副作用的“功能暂未开放”反馈。没有权威来源时，禁止显示 `Delivered`、`Read`、`Deleted`、`Retrying`、用户头像照片、Presence、成员角色、置顶或收藏。消息状态仅为 `Sending`、`Sent`、`Failed`。

## 4. 响应式与验证契约

| 宽度 | Rail | Conversation | 抽屉 |
| --- | --- | --- | --- |
| `>=1400` | 72px | 340px | 可打开，360px |
| `1100–1399` | 72px | 320px | 关闭 |
| `900–1099` | 64px | 280px | 关闭 |

窗口最小为 900×520，标题栏为 48px。测试需覆盖资源加载和 token、每个 intent 仅一次、导航和键盘、搜索/筛选、未开放入口无副作用、标题栏生命周期，以及既有消息/附件/搜索/成员/账户 shell 回归。每个控件拆分后先运行定向测试，最终以 Full、Release、快照和真实 Windows 窗口验收收口。
