# RelayCove UI 重构落地方案 v1.1（可执行规格）

> v1.0 保留为历史设计输入；本文件是 rc.25 唯一可执行的 UI 规格。产品与架构以 [`../../RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md) 为准。

## 1. 目标与不可变边界

rc.25 将 WPF 客户端重构为“自绘标题栏 + Navigation Rail + Conversation Panel + Chat Panel + 条件式成员抽屉”的蓝白桌面聊天工作区。它只改变展示层、键盘可达性与局部交互反馈；既有登录、同步、搜索、附件、成员管理、更新、通知、关闭到托盘和可靠发送语义必须保持不变。

- 不修改 Server、Shared DTO、SignalR、SQLite schema、Updater 或消息可靠性协议。
- 不新增联系人目录、通知中心、文件中心、Emoji、语音、主动截图、反应、转发、收藏、置顶或删除消息。
- 不伪造用户照片、Presence、成员角色、置顶/收藏/通知数据或效果图中的演示业务数据。

## 2. 冻结视觉 token

| 用途 | 固定值 |
| --- | --- |
| Primary | `#1677D2` |
| Primary Hover / Pressed / Soft | `#0958D9` / `#003EB3` / `#EAF3FF` |
| Canvas / Surface / Border | `#F5F7FA` / `#FFFFFF` / `#E5EAF0` |
| Text Primary / Secondary / Neutral | `#1F2328` / `#667085` / `#8F9AA5` |
| Danger / Success / Warning | `#D92D20` / `#12B76A` / `#F79009` |

- 字体固定 `Segoe UI`；不引入字体、图标包或 UI 框架。
- 圆角：面板 12px、卡片/按钮 8px、输入 10px；图标使用 20px/24px 的 WPF `Geometry/Path`。
- 蓝色是唯一品牌主色。绿色只表达真实的成功、完成或在线等语义，不能替代品牌色。
- 不实现暗色主题、皮肤系统、复杂动画或高度自定义布局。

## 3. 布局与窗口规范

| 窗口宽度 | Rail | Conversation | 成员抽屉 |
| --- | --- | --- | --- |
| `>=1400` | 72px | 340px | 可打开，360px |
| `1100–1399` | 72px | 320px | 强制关闭 |
| `900–1099` | 64px | 280px | 强制关闭 |

- `MinWidth=900`、`MinHeight=520`；Chat Panel 使用全部剩余宽度，Composer 与附件、`@`、发送始终可达。
- 标题栏固定 48px，以 WPF `WindowChrome` 保留 resize border、DPI 与最大化工作区。
- 支持拖动、双击最大化/还原、最小化、最大化/还原、Alt+Space；关闭按钮只调用 `Window.Close()`，由现有 `App.OnMainWindowClosing` 决定隐藏到托盘。真正退出与更新交接只能走原生命周期入口。
- 小于 1400px 的成员入口显示可访问的说明提示，不得打开或挤压抽屉；登录页小于 1100px 时仅隐藏辅助品牌说明。

## 4. 功能映射与真实状态

| 区域 | 可用入口 | 未开放但可点击的入口 |
| --- | --- | --- |
| Rail | 头像→账户/设置、聊天→All、频道→Channels、设置 | 联系人、通知、文件、更多 |
| Chat Header | 成员→既有抽屉、搜索→既有显式服务端搜索 | 置顶、会话通知、更多 |
| Message / Composer | 回复、复制、条件式重试、附件、`@`、正文、发送 | Emoji、语音、主动截图、发送下拉、反应、转发、收藏、消息置顶、删除 |

未开放入口采用弱化但可交互样式，具有 ToolTip 和 `AutomationProperties.Name`。点击显示约三秒的非模态“功能暂未开放”提示；连续点击重置计时，且不得触发网络、数据库、文件、发送、选择或导航副作用。

消息只展示既有权威 `Sending`、`Sent`、`Failed`；不得新增或文案暗示 `Delivered`、`Read`、`Deleted`、`Retrying`。失败重试保留原 `ClientMessageId` 和可靠发送路径。图片继续经过既有授权、下载、完整性校验、缓存和安全解码边界，禁止远端 URL 直接绑定 `Image.Source`。

## 5. 组件边界与实施切片

资源字典依次新增 `ClientTheme.xaml`、`ClientIcons.xaml`、`ClientControls.xaml`，由 `App.xaml` 合并；相同 token 不得在控件重复硬编码。展示控件可逐步拆为 `TitleBarControl`、`NavigationRailControl`、`ConversationPanelControl`、`ChatHeaderControl`、`MessageListControl`、`ComposerControl`、`SettingsPanelControl`、`UiNoticeHost`。

控件只通过 DependencyProperty 和 RoutedEvent 传递展示状态与用户意图，不能持有 HTTP、SignalR、SQLite、附件、更新或账户 runtime。`MainWindow` 保留 coordinator、取消、generation、Dispatcher、selection lease 和焦点回退；每次只移动一个组件边界并先运行定向验证。

| 切片 | 内容 | 完成条件 |
| --- | --- | --- |
| S0 | 文档、Fast 基线、before 快照 | 文档无冲突；Fast 通过；before 3/3 已记录 |
| S1 | Theme、Icons、Controls | 资源加载与既有窗口测试通过，无行为变化 |
| S2 | 自绘标题栏与生命周期 | 命令、键盘、托盘关闭与更新交接回归通过 |
| S3 | Rail、会话搜索/筛选、设置入口 | 导航、过滤、未开放反馈、虚拟化通过 |
| S4 | Header、消息流、Composer | 消息、附件、回复、提及、发送、重试回归通过 |
| S5 | 登录与次级界面 | 搜索、成员、设置、更新、查看器及状态快照通过 |
| S6 | 快照、视觉复核、Full | 无 P0/P1/P2；Full/Release 通过 |
| S7 | rc.25 双构建 | 两个 ZIP 字节一致且离线验证通过 |

## 6. 验收矩阵与停止条件

after 快照至少覆盖登录（900×520、1280×720）、主聊天（900×520、1280×720、1600×900 抽屉、1920×1080）、Composer 压力状态、搜索、设置、强制更新和图片查看器。标准宽度误差不超过 4px；关键控件不得裁切或重叠，列表继续虚拟化。

完成 UI 代码后运行 Fast、Full、Release、WPF 快照与只读独立复核；真实 Windows 仍须在 100%/125%/150% DPI 下验证标题栏、边缘缩放、Alt+Space、键盘、托盘关闭/恢复、真正退出和更新交接。无法实测的项目必须标注 `未验证`。

停止并询问：Fast 或既有定向测试稳定失败；出现 `plan/` 以外无关改动；需要改变协议、可靠性、附件安全或更新流程；标题栏绕过既有生命周期；需要大型依赖；或参考图与真实能力发生不可消解的歧义。
