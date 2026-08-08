# RelayCove 1.0.0-rc.25 UI 重构详细执行方案

## 1. 文档状态

- **目标版本：** `1.0.0-rc.25`
- **开发阶段：** stage-20
- **工作分支：** `agent/stage-20-rc25-ui-redesign`
- **文档用途：** 后续模型实施 rc.25 UI 重构时的唯一执行入口
- **产品与架构真源：** [`../../RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md)
- **既有 UI 约束：** [`../ui-design-guidelines.md`](../ui-design-guidelines.md)
- **设计输入：** [`../../plan/rc.25/`](../../plan/rc.25/)
- **可执行实施规格：** [`RelayCove_UI_Redesign_Implementation_Spec_v1.1.md`](../../plan/rc.25/RelayCove_UI_Redesign_Implementation_Spec_v1.1.md)
- **可执行组件规格：** [`RelayCove_UI_Component_Spec_v1.1.md`](../../plan/rc.25/RelayCove_UI_Component_Spec_v1.1.md)
- **可执行验收清单：** [`RelayCove_UI_Acceptance_Checklist_v1.1.md`](../../plan/rc.25/RelayCove_UI_Acceptance_Checklist_v1.1.md)
- **状态页：** [`STATUS.md`](STATUS.md)
- **工作流：** [`WORKFLOW.md`](WORKFLOW.md)

本方案冻结 rc.25 的产品边界、视觉方向、组件边界、实施切片、验证方法和交付终点。后续执行者不得只依据效果图或聊天记录自行补充功能。

## 2. 目标与成功定义

rc.25 是 Windows WPF 客户端 UI 专项版本。目标是尽量复刻参考图的信息层级、布局密度、圆角、图标语言和交互反馈，同时建立 RelayCove 自己的蓝白视觉体系。

完成标准：

1. 登录、主聊天、搜索、成员/频道、设置、更新、图片查看器及主要空/加载/错误状态使用统一设计系统。
2. 主窗口采用“自绘标题栏 + Navigation Rail + Conversation Panel + Chat Panel + 条件式成员抽屉”。
3. 现有消息、同步、搜索、附件、成员管理、更新、通知、关闭到托盘和可靠性语义不变。
4. 未开放功能保持可见和可点击，统一反馈“功能暂未开放”，但不得产生网络、持久化或业务状态副作用。
5. 定向测试、Fast、Full、Release、WPF 快照和独立复核通过。
6. 从干净精确 HEAD 双构建两个字节一致的 `RelayCove.Client-1.0.0-rc.25-win-x64.zip`，并完成离线包验证。
7. 本任务不生成线上更新 manifest，不推送、部署或切换内部更新通道。

## 3. 当前仓库事实

### 3.1 已验证实现形态

- Client 当前只有一个主要 WPF 视图：`src/RelayCove.Client/MainWindow.xaml`，约 1,898 行；对应 code-behind 约 5,205 行。
- `App.xaml` 当前没有全局资源字典；颜色、Style 和 ControlTemplate 主要位于 `MainWindow.xaml`。
- 登录后主区当前为 272px 会话栏加弹性聊天区；成员界面是右侧 Overlay，不存在独立 Navigation Rail。
- 当前主色为靛蓝 `#4F46E5`，部分界面又硬编码 `#1677D2`，视觉 token 尚未统一。
- 会话列表已实现公开频道、私有频道、私聊分组、虚拟化、类型图标、消息预览、时间、未读和静音标签。
- 消息流已实现连续消息合并、日期线、新消息线、回复、链接、图片、文件、复制和失败重试。
- Composer 已实现正文、附件、`@`、回复和发送；没有 Emoji、语音或主动截图功能。
- 当前消息发送状态只有 `Sending`、`Sent`、`Failed`；失败重试必须沿用原 ClientMessageId 和既有可靠发送流程。
- 本地成员数据没有逐成员频道角色字段；只能展示真实能力标签，不能伪造角色。
- 当前已有基于 WPF `RenderTargetBitmap` 的 1280×720、1600×900、1920×1080 快照入口。
- Client 当前唯一图片资产为 `Assets/RelayCove.ico`；参考图集不是可直接消费的独立图标文件。

### 3.2 当前工作区与基线

- 创建本文档前分支为 `agent/stage-19-rc24-stabilization`，仅 `plan/` 为未跟踪输入。
- 已切换到 `agent/stage-20-rc25-ui-redesign`；未发现其他工作区修改。
- `已验证`：本轮 S0 Fast 为 0 警告、0 错误；Shared 70、Server 353、Client 1,178、Updater 38，共 1,639 项通过。基准为 `baaae88`。
- `已验证`：before 主窗口快照 3/3 已存在：1280×720、1600×900、1920×1080（见 [`docs/ui/rc.24/`](../ui/rc.24/)）。after 快照、Full、Release 与原生窗口验收仍为 `未验证`，留待对应切片。

## 4. 冻结的产品与视觉决策

### 4.1 品牌与视觉 token

| 用途 | 固定值 |
| --- | --- |
| Primary | `#1677D2` |
| Primary Hover | `#0958D9` |
| Primary Pressed | `#003EB3` |
| Primary Soft | `#EAF3FF` |
| Canvas | `#F5F7FA` |
| Surface | `#FFFFFF` |
| Border | `#E5EAF0` |
| Text Primary | `#1F2328` |
| Text Secondary | `#667085` |
| Neutral | `#8F9AA5` |
| Danger | `#D92D20` |
| Success | `#12B76A` |
| Warning | `#F79009` |

- 绿色只用于在线、成功、完成等语义状态，不作为品牌主色。
- 字体使用 `Segoe UI`，不新增字体文件或运行时字体依赖。
- 面板圆角 12px，卡片 8px，按钮 8px，输入框 10px。
- 标准图标画布为 20px/24px，线宽约 1.5px；使用 WPF `Geometry/Path`，不得裁切参考 PNG 模拟控件。
- 不实现暗色主题、复杂动画、皮肤系统或高度自定义布局。

### 4.2 功能映射

#### Navigation Rail

| 入口 | rc.25 行为 |
| --- | --- |
| 用户头像 | 打开账户/设置面板 |
| 聊天 | 可用；展示全部会话 |
| 联系人 | 未开放反馈 |
| 频道 | 可用；本地过滤公开和私有频道 |
| 通知 | 未开放反馈；系统通知状态继续在设置中展示 |
| 文件 | 未开放反馈 |
| 设置 | 可用；打开设置面板 |
| 更多 | 未开放反馈 |

#### Chat Header

| 入口 | rc.25 行为 |
| --- | --- |
| 成员 | 可用；复用现有成员抽屉 |
| 搜索 | 可用；复用现有显式服务端消息搜索 |
| 置顶 | 未开放反馈 |
| 会话通知 | 未开放反馈 |
| 更多 | 未开放反馈 |

#### Message 与 Composer

| 入口 | rc.25 行为 |
| --- | --- |
| 回复、复制、条件式重试 | 可用；复用现有流程 |
| 附件、`@`、正文、发送 | 可用；复用现有流程 |
| Emoji、语音、主动截图、发送下拉 | 未开放反馈 |
| 表情回应、转发、收藏、消息置顶、删除 | 未开放反馈 |

未开放入口不能使用 `IsEnabled=false`，否则无法点击反馈。统一使用“弱化但可交互”样式、ToolTip 和 `AutomationProperties.Name`。点击后显示约三秒的非模态提示，连续点击重置计时，不触发任何网络、数据库、文件、发送或导航副作用。

### 4.3 不得伪造的状态

- 不新增 `Delivered`、`Read`、`Deleted`、`Retrying` 等没有现有权威来源的消息状态。
- 不伪造用户照片头像、在线状态、忙碌状态、成员角色、置顶状态、收藏状态或通知中心数据。
- 不把效果图中的演示用户名、频道名、附件或机器人内容加入生产数据。
- 图片仍必须经过现有安全下载、完整性校验和解码边界，禁止从远端 URL 直接绑定 `Image.Source`。

## 5. 响应式布局规范

### 5.1 窗口与标题栏

- 保留 `MinWidth=900`、`MinHeight=520`。
- 自绘标题栏高度固定 48px。
- 使用 WPF `WindowChrome` 保留系统 resize border、DPI 和最大化工作区行为。
- 标题栏支持拖动、双击最大化/还原、最小化、最大化/还原、Alt+Space 系统菜单。
- 关闭按钮只能调用正常 `Window.Close()`；由现有 `App.OnMainWindowClosing` 决定隐藏到托盘，不能直接 Shutdown 或 Hide。
- 真正退出和更新交接继续使用现有生命周期入口。

### 5.2 主布局断点

| 窗口宽度 | Navigation Rail | Conversation Panel | 成员抽屉 |
| --- | --- | --- | --- |
| `>=1400` | 72px | 340px | 可打开，360px |
| `1100–1399` | 72px | 320px | 强制关闭 |
| `900–1099` | 64px | 280px | 强制关闭 |

- Chat Panel 始终使用剩余宽度，不能被固定到不可用宽度。
- 小窗口可压缩辅助文字、会话预览和 Composer 图标间距，但正文输入、附件、`@` 和发送必须可达。
- 小于 1400px 时点击成员入口继续显示可访问提示，不能挤压聊天区。
- 登录界面小于 1100px 时隐藏辅助品牌说明区，登录表单保持完整。

## 6. 组件与接口方案

### 6.1 资源字典

在 `src/RelayCove.Client/Resources/` 新增：

- `ClientTheme.xaml`：Brush、颜色、字号、间距、圆角、阴影和尺寸 token。
- `ClientIcons.xaml`：所有 Navigation、Header、Message、Composer 和窗口图标 Geometry。
- `ClientControls.xaml`：Button、ToggleButton、TextBox、PasswordBox、ListBoxItem、Badge、Card、ScrollBar 和焦点样式。

`App.xaml` 合并这三个字典。控件不得重新硬编码相同颜色或尺寸；语义例外必须使用命名资源。

### 6.2 纯展示控件

在 `src/RelayCove.Client/Controls/` 逐步新增：

- `TitleBarControl`
- `NavigationRailControl`
- `ConversationPanelControl`
- `ChatHeaderControl`
- `MessageListControl`
- `ComposerControl`
- `SettingsPanelControl`
- `UiNoticeHost`

边界规则：

- 控件只接收展示状态、集合和可用性，通过 DependencyProperty 和 RoutedEvent 输出用户意图。
- 控件不得持有 HTTP、SignalR、SQLite、附件服务、更新协调器或账户 runtime。
- `MainWindow` 继续拥有现有 coordinator 适配、异步调用、取消、generation、Dispatcher、selection lease 和焦点回退。
- 每拆出一个控件，先迁移对应 XAML 和最小事件转发，再运行定向测试；禁止一次性重写整个 MainWindow。

### 6.3 Client-only 展示类型

新增以下 Client 内部类型，不放入 Shared：

- `ClientUiFeatureId`：稳定标识联系人、通知、文件、置顶、Emoji、语音等入口。
- `ClientUiFeatureAvailability`：`Available`、`Unavailable`。
- `ClientUiFeatureDescriptor`：FeatureId、显示名、可用性、提示文本。
- `ClientNavigationSection`：Chat、Contacts、Channels、Notifications、Files、Settings、More。
- `ClientConversationFilter`：All、Unread、Channels、Direct。

主要 RoutedEvent：

- `NavigationRequested`
- `ConversationFilterChanged`
- `ConversationSelectionRequested`
- `MembersRequested`
- `SearchRequested`
- `SendRequested`
- `UnavailableFeatureRequested`

本任务不修改公共 API、Shared DTO、SignalR、数据库、Server 或 Updater 业务接口。

## 7. 界面实施要求

### 7.1 登录页

- 使用自绘标题栏、蓝白品牌区和白色登录卡片。
- 保留服务器地址、用户名、密码、登录状态、错误和重试行为。
- 小窗口隐藏非必要品牌说明，不隐藏错误或主要操作。

### 7.2 Navigation 与会话栏

- Navigation Rail 顶部为品牌和用户头像，中部为入口，底部为设置和更多。
- 会话栏增加本地搜索，按 `Name` 与 `Preview` 做 `OrdinalIgnoreCase` 包含匹配，不请求服务端。
- `Ctrl+K` 聚焦会话搜索框。
- 提供 All、Unread、Channels、Direct 本地筛选。
- Chat 导航选择 All；Channels 导航选择 Channels。
- 保留固定三组、分组折叠状态、创建频道入口和 recycling virtualization。
- 搜索和筛选不得删除会话、修改未读或改变权威选择；过滤掉当前选择时 Chat Panel 保持当前会话，直到用户选择其他会话。

### 7.3 Chat Header 与消息流

- Header 展示真实标题、描述/提示和成员数量。
- 消息搜索继续显式点击或 Enter 后请求服务端，不改 `DEC-053`。
- 消息流继续使用无气泡布局、头像缩写、昵称、时间和正文。
- 连续消息合并、日期线、新消息线、回复、提及、链接、图片和文件卡片不得回归。
- Hover 操作直接保留回复、复制和重试；未开放操作只显示提示。

### 7.4 Composer

- Composer 固定在 Chat Panel 底部。
- 回复、`@` 候选、已选附件、上传进度和状态文本使用统一卡片样式。
- 继续支持多行输入、Enter 发送、Ctrl+Enter 换行、附件选择/拖入/粘贴、`@` 和单附件移除。
- 主发送按钮执行现有发送；下拉箭头只显示“更多发送方式暂未开放”。
- 发送成功后的正文和附件清理语义保持不变。

### 7.5 设置与次级界面

- 设置面板承载显示名、连接状态、服务器地址、通知状态、未读摘要、检查更新、重连和退出账户。
- 搜索界面改为统一模态卡片，保留 Current/Global、显式查询、取消、结果导航和高亮语义。
- 成员/频道界面保持右侧抽屉，保留创建频道、成员搜索、添加、移除和权限限制。
- 强制更新界面保持模态和焦点圈闭；更新下载、取消、重试、应用和退出语义不变。
- 图片查看器保持暗色遮罩、安全尺寸、关闭/Escape 和焦点回退。
- 空、加载、断线、错误状态只更新视觉，不改变底层状态来源或重试触发方式。

## 8. 纵向实施切片

| 切片 | 内容 | 完成条件 |
| --- | --- | --- |
| S0 | 基线、before 快照、任务记录和文档 v1.1 | Fast 通过；文档无冲突；工作区范围明确 |
| S1 | Theme、Icons、Controls 资源字典 | 资源加载测试通过；现有窗口可运行；无视觉行为变化 |
| S2 | 自绘标题栏与窗口生命周期 | 窗口命令、键盘、关闭到托盘和更新关闭路径测试通过 |
| S3 | Navigation Rail、会话搜索/筛选、设置入口 | 导航、过滤、占位反馈和虚拟化测试通过 |
| S4 | Chat Header、消息流、Composer | 消息、附件、回复、提及、发送和重试回归通过 |
| S5 | 登录、搜索、成员、更新、图片查看及状态界面 | 所有次级界面快照和原行为回归通过 |
| S6 | 三档/紧凑快照、视觉复核、Full 与独立审查 | 无 P0/P1/P2；Full/Release 通过 |
| S7 | rc.25 双构建与离线包验证 | 两份 ZIP 字节一致，记录 commit、长度和 SHA-256 |

建议提交边界：

1. `Add rc25 UI specification`
2. `Add client visual foundation`
3. `Add custom window chrome`
4. `Add rc25 navigation shell`
5. `Restyle chat workspace`
6. `Restyle client overlays`
7. `Add rc25 visual verification`

每个提交前必须运行对应定向测试；最终包只能来自所有修改已提交的干净 HEAD。

## 9. 自动化测试计划

### 9.1 新增测试

- Theme token 精确色值、资源键唯一和资源字典可加载。
- 所需矢量图标资源存在，控件引用不产生资源解析异常。
- Navigation Rail 选择态、键盘操作和 AutomationProperties。
- 本地会话搜索对名称/预览的包含匹配、大小写和清空恢复。
- All、Unread、Channels、Direct 筛选，以及当前选择不被过滤副作用清除。
- 未开放入口返回精确 `ClientUiFeatureId`、显示提示、连续点击重置计时且不调用业务处理器。
- 自绘标题栏最小化、最大化、还原、系统菜单和关闭命令。
- Close 仍进入 App Closing/隐藏到托盘流程；更新交接和真正退出不得被窗口按钮绕过。
- 控件拆分后的事件只触发一次，不因 XAML 重挂导致重复处理。

### 9.2 必须保留的既有回归

- `ClientUiSnapshotTests`
- `MainWindowNavigationPresentationTests`
- `ClientSearchPresentationTests`
- `MainWindowAttachmentDownloadPresentationTests`
- `MainWindowAttachmentImagePresentationTests`
- `ClientUpdateHandoffTests`
- 会话列表、消息列表、Composer、成员管理及账户 shell 定向测试
- Fast、Full 和 Release 全量验证

## 10. 快照与视觉验收矩阵

至少生成以下 after 快照：

| 场景 | 尺寸/状态 |
| --- | --- |
| 登录页 | 900×520、1280×720 |
| 主聊天紧凑布局 | 900×520 |
| 主聊天标准布局 | 1280×720 |
| 主聊天成员抽屉 | 1600×900 |
| 主聊天宽屏 | 1920×1080 |
| Composer 压力状态 | 1280×720，回复 + `@` + 10 附件 |
| 搜索界面 | 1600×900 |
| 设置界面 | 1280×720 |
| 强制更新 | 1280×720 |
| 图片查看器 | 1600×900 |

验收规则：

- 标准宽度误差不超过 4px。
- 关键控件不裁切、不重叠；Chat Panel 和 Composer 始终可用。
- 1280px 以下成员抽屉必须关闭，不能挤压输入区。
- 会话和消息列表继续使用虚拟化。
- 蓝白品牌、信息层级、间距、圆角和图标语言应接近参考图。
- 因真实功能、蓝色品牌、WPF 字体栅格化和 DPI 导致的差异允许存在，不进行跨 DPI 逐像素等值断言。
- before/after 文件、尺寸、场景和人工复核结论写入 stage-20 任务记录。

## 11. Windows 原生窗口验收

自绘标题栏不能只依赖 `RenderTargetBitmap` 判定通过。必须在真实 Windows 环境记录：

- 100%、125%、150% 缩放。
- 普通、最大化、还原和最小化。
- 鼠标拖动、标题栏双击和边缘缩放。
- Alt+Space 系统菜单。
- 键盘 Tab、Enter、Space 和 Escape。
- 关闭按钮隐藏到托盘、托盘恢复和真正退出。
- 强制更新状态下关闭、退出和交接行为。

无法执行的项目必须标记为 `未验证`。可以生成候选包，但不得把候选包描述为完整发布验收通过。

## 12. 最终验证与可复现包

### 12.1 代码验证

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
git diff --check
```

Full 必须为 0 警告、0 错误，全部测试通过。UI 变化完成后安排一个只读 Codex reviewer，重点检查：

- 关闭到托盘和更新交接。
- UI Dispatcher、取消和迟到回调。
- 虚拟化、焦点、键盘和自动化属性。
- 未开放入口没有业务副作用。
- 没有改变 Shared/Server/数据库/可靠发送语义。

### 12.2 双构建

发布包必须来自干净、已提交的精确 HEAD，正式包不得使用 `-AllowDirty`。

```powershell
$releaseCommit = git rev-parse HEAD
git status --porcelain

pwsh ./scripts/publish-client.ps1 `
  -Version 1.0.0-rc.25 `
  -OutputRoot ./artifacts/rc25-build-a

pwsh ./scripts/publish-client.ps1 `
  -Version 1.0.0-rc.25 `
  -OutputRoot ./artifacts/rc25-build-b

pwsh ./scripts/verify-client-release.ps1 `
  -Version 1.0.0-rc.25 `
  -OutputRoot ./artifacts/rc25-build-a `
  -CompareOutputRoot ./artifacts/rc25-build-b `
  -ExpectedCommit $releaseCommit
```

记录：

- 精确 commit 和 SDK。
- ZIP 文件名、长度和 SHA-256。
- 两份 ZIP 字节一致。
- manifest、包内文件 hash、PE x64、自包含运行时和秘密排除检查通过。

## 13. 强制停止条件

出现以下任一情况时停止实现并保留现场：

- Fast 基线或既有定向测试稳定失败，且不是本任务范围内已知问题。
- 出现 `plan/` 之外的无关工作区修改。
- 需要修改 Server、Shared DTO、SignalR、SQLite schema、消息可靠性、附件安全或更新协议。
- 自绘标题栏需要绕过现有关闭到托盘、真正退出或更新交接生命周期。
- 需要新增大型 UI 框架、图标包、字体或其他生产依赖。
- 参考图与真实功能发生多种会显著改变结果的解释。
- 必要验证无法运行，且没有证据证明改动安全。

## 14. 明确非目标

- 不实现联系人目录、通知中心、文件中心、Emoji、语音、主动截图、反应、转发、收藏、置顶或删除消息。
- 不实现真实头像、Presence、逐成员角色或机器人系统。
- 不实现暗色主题、主题切换、复杂动画、皮肤市场或高度自定义布局。
- 不改变 Server、Shared、Updater 业务逻辑和数据库。
- 不生成线上 update manifest，不推送、不部署、不切换更新通道。

## 15. 后续模型接手指令

后续模型开始实施时必须：

1. 完整阅读 `AGENTS.md`、工程方案 §9/阶段 8/§22.3、`docs/ai/STATUS.md`、`docs/ai/WORKFLOW.md`、本文档和 `plan/rc.25` 参考资料。
2. 检查分支必须为 `agent/stage-20-rc25-ui-redesign`，工作区除 `plan/` 外不得有未知修改。
3. 新建 stage-20 活动任务记录，并把当前现状、基准 commit、允许修改范围和停止条件写入记录。
4. 重新运行 Fast；只有真实通过后才能标记绿色基线。
5. 先阅读 rc.25 三份 [v1.1 可执行规格](../../plan/rc.25/RelayCove_UI_Redesign_Implementation_Spec_v1.1.md)和 UI 设计约束，再进入 S1；不要直接从效果图开始改 XAML。
6. 严格按 S1–S7 逐个纵向切片实施，单次只移动一个组件边界。
7. 不得修改协议或伪造效果图中的业务能力。
8. 完成后更新任务记录和 `STATUS.md`，再进行干净 HEAD 双构建。

当前文档准备任务只创建执行方案，尚未开始产品代码修改、完整基线验证、截图验收、提交、打包或发布；这些状态全部保持 `未验证`。
