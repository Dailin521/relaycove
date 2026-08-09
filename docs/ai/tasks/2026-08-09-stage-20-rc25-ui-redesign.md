# 任务：stage-20 rc.25 UI 重构文档与 S0 基线

## 任务定义

- **状态：** 进行中（S0–S14 的 UI、图片预览、组件化与自动化切片已完成；Fast/Full 已通过，待最终已提交 HEAD 的双构建、离线校验与独立只读复核）
- **基准提交：** `baaae8813d518bee8364a4977174a95e97127eed` (`baaae88`)
- **工作分支：** `agent/stage-20-rc25-ui-redesign`
- **相关方案章节：** [工程方案 §9](../../../RelayCove_工程落地方案.md#9-客户端工程设计)、阶段 8、§22.3；[rc.25 执行方案](../RC25_EXECUTION_PLAN.md)

### 目标

冻结 rc.25 的蓝白 WPF UI 可执行规格，记录可复现的 S0 基线与 before 快照，并把后续实施限制为不改变现有聊天、可靠性和窗口生命周期语义的纵向切片。

### 已知事实

- `已验证`：本轮 Fast 基线为 0 警告、0 错误；测试通过 Shared 70、Server 353、Client 1,178、Updater 38，共 1,639 项。
- `已验证`：基准提交为 `baaae88`，分支为 `agent/stage-20-rc25-ui-redesign`。
- `已验证`：before 快照 3/3 已存在，覆盖 1280×720、1600×900、1920×1080 主窗口外观：[`1280×720`](../../ui/rc.24/main-window-outer-1280x720.png)、[`1600×900`](../../ui/rc.24/main-window-outer-1600x900.png)、[`1920×1080`](../../ui/rc.24/main-window-outer-1920x1080.png)。
- `已验证`：现有客户端权威消息状态只有 `Sending`、`Sent`、`Failed`；Emoji、语音、主动截图、消息删除等不是现有能力。

### 假设

- `假设`：rc.24 三张主窗口快照可作为 rc.25 S0 的 before 对比基准；登录和次级界面 before 快照需在相应实施切片开始前补充。

### 范围

- 必须实现：
  - 保留三份 v1.0 原文，新增同名 v1.1 实施、组件和验收规格。
  - 固定蓝白 token、断点、自绘标题栏、真实功能映射、不可伪造状态和验收矩阵。
  - 渐进式接入 Client 主题、标题栏、导航与本地会话筛选，并在后续切片重排既有界面。
- 允许修改：
  - `docs/`、`plan/rc.25/`、`RelayCove_工程落地方案.md`
  - `src/RelayCove.Client/` 与 `tests/RelayCove.Client.Tests/`（仅展示与验证边界）
  - `scripts/verify-client-release.ps1`（仅与发布器统一秘密路径拒绝规则）
- 明确不做：
  - 不修改 Shared DTO、Server、SignalR、SQLite、消息可靠性、附件安全或更新协议。
  - 不推送、部署或发布更新通道。

### 验收标准

- [x] v1.0 文件未修改，三份 v1.1 规格直接可执行且互相不冲突。
- [x] 绿色品牌、`Delivered`、`Deleted`、`Retrying` 和 Emoji 等与仓库事实冲突的描述已在 v1.1 中替换为真实能力或无副作用的未开放反馈。
- [x] rc.25 执行方案直接链接 v1.1，工程方案 §9.2 与 UI 设计约束采用 Rail + Conversation + Chat + 条件成员抽屉。
- [x] 文档差异和本地 Markdown 链接检查通过。

### 验证命令

```powershell
git diff --check
# 本地 Markdown 链接检查：解析修改 Markdown 中的相对文件链接并验证目标存在。
```

### 停止并询问

- 需要修改 Server、Shared DTO、SignalR、SQLite、消息可靠性、附件安全或更新协议。
- 自绘标题栏无法复用现有关闭到托盘、真正退出或更新交接生命周期。
- 参考图要求与现有真实功能发生不能由“未开放反馈”解决的实质冲突。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 新增三份 rc.25 v1.1 规格，作为唯一可执行规格；v1.0 保留为历史输入。
- 以蓝白品牌和真实能力重写 UI 约束，补充可访问的未开放反馈、窗口断点、成员抽屉、生命周期与快照验收边界。
- 将工程方案 §9.2 更新为四区域桌面布局，并让 rc.25 执行入口链接 v1.1 和本轮 S0 证据。
- S1：接入 `ClientTheme.xaml`、`ClientIcons.xaml`、`ClientControls.xaml` 及主题资源验证。
- S2：以 `WindowChrome` 和 `TitleBarControl` 实现 48px 自绘标题栏；关闭路径仍调用正常 `Window.Close()`。
- S3：接入导航轨、会话本地搜索/筛选、未开放功能反馈及其回归测试；修复筛选隐藏当前会话与搜索/通知定位不能穿透筛选的状态分裂。
- S4–S6：继续完成主窗口及覆盖层的视觉重排，并生成四档主窗口 after 快照作为渐进式视觉证据；这些快照不替代最终 Full/Release 或真实 Windows 验收。
- S7（局部）：为 Composer 输入卡增加可访问的垂直调整热区；仅拉伸正文区，严格保留消息流最小可视空间，并以定向 WPF 测试和调整后快照验证。
- S8–S10：补齐成员/设置/搜索/图片查看器/强制更新覆盖层的互斥与焦点回退，补齐次级界面 after 快照；随后按参考图收敛会话栏、Header 与 Composer 的视觉密度。
- S11：根据独立视觉复核修复最小窗口裁切、根级搜索遮罩、设置关闭按钮和辅助文本对比度；二次复核确认 P1 清零。
- S12：根据产品复审进一步收敛为内容优先的三列聊天工作区：消息操作仅在 hover/失败状态显现，搜索改为带图标和快捷键提示的会话搜索卡，Composer 和 Header 采用一致的聊天内容宽度，成员抽屉仅遮罩聊天列；复杂回复/10 附件态压缩为完整两行 chip，并保留顶缘拖拽。
- S13：将 UI 质量门扩展至全部子界面。Rail、会话栏、Header、标题栏和设置抽屉统一为紧凑的蓝白层级；登录品牌区使用受控蓝色渐变和半透明连接状态卡；强制更新为居中紧凑卡，查看器关闭操作与深色查看器匹配。
- S13 图片：单图消息改为图片主导的直接预览，点击图片可进入既有查看器，加载/失败只保留轻量状态。经独立视觉复核后移除固定 16:9 框，横图、方图与竖图均在 360×280 的受限范围内保留自身比例和可见辅助信息。
- S13 发布校验：离线 verifier 的 credential/token 路径规则与发布器对齐，并以真实 ZIP 篡改回归验证拒绝 `credentials.bin` 与 `auth-access_token.bin`。
- S14：完成 `ConversationPanelControl`、`MessageListControl` 和 `ComposerControl` 的真实视觉树提取。控件只经 DependencyProperty、RoutedEvent 和内部展示访问器与壳通信；`MainWindow` 继续持有 coordinator、可靠发送、附件下载/解码、取消、generation、Dispatcher、滚动与 read receipt。
- S14：以独立审计结果重排全部次级界面：搜索模态具有稳定结果区和空/加载/错误文本位，成员/频道以 380px 聊天区覆盖抽屉呈现而非第四列，设置抽屉承载可选更新的下载/取消/安装入口；登录、强制更新与图片查看器复用统一 token、焦点与关闭图标语言。
- S15：把 UI 质量门延展到全部可达次级界面。登录卡片增加清晰的工作区登录层级；搜索和成员目录采用图标化输入及稳定空态；成员抽屉去除全屏暗幕并与设置面板保持同一轻量覆盖语言，仍不形成第四列；图片查看器在图片尚未可用时提供明确的安全预览等待态。
- S15：修复独立复核 P1：所有主 CTA（登录、更新下载/应用、设置可选更新）在 hover/pressed 使用深蓝背景并保留白字；`MessageListControl` 恢复回复引用、安全链接、真实发送状态以及回复/复制/重试的 RoutedEvent 路由。常规消息操作栏仅 hover 出现，失败消息保留可见重试入口。
- S15 终局复核：发现并修复组件提取造成的单图预览元数据重叠，以及 900×520 登录卡的主按钮裁切；图片、元数据不相交的几何断言和紧凑登录标题隐藏断言已加入快照回归。独立复核未发现 Server、Shared、Updater、协议、附件安全或可靠发送所有权改动。
- S16：以三路独立审查收口全部次级界面。成员抽屉以透明命中层阻断背景会话切换，并在重新加载前清除旧成员/目录及操作模型；邀请和移除进一步确认展示对象仍属于当前会话。搜索结果具备唯一 UIA 名称，搜索加载、零结果与错误主标题可辨；设置页将可选更新交接失败同步为可见 live status；图片查看器明确区分加载与安全预览失败，并阻止 `Ctrl+K` 将焦点逃逸到遮罩下方。新增查看器加载/失败快照与更新交接、可访问搜索结果回归。
- S16 独立复核：发现旧会话操作在完成后仍可能取消新会话加载的并发 P1，已在回调处复核当前抽屉、会话与 coordinator 后才更新界面或重载成员；搜索结果 UIA 名称改用不泄露内部 ID 的稳定序号，避免同会话同分钟结果碰撞。
- S17 交互回归：消息发送引发的会话列表重发布不再被当作一次重新选择，只有会话 ID 实际变化才刷新成员；成员抽屉打开和成员增删成功仍保留显式刷新。提及候选成功选中后会取消候选搜索、关闭面板并把焦点交还输入框。消息同窗口重绘不再按“顶部补历史”补偿滚动偏移，消息行被点击选中时维持中性外观，避免默认蓝色整行高亮。
- S18 聊天体验：首次获得 Ready 会话快照时选择按活动时间排序的首项；私聊筛选展示正常成员目录（排除自身），点击复用既有 Direct 创建/获取并选中返回会话；bootstrap 空库与管理员在同一事务内创建 `general` 公开频道及其创建者成员关系。消息列表保持回收虚拟化，短历史贴近 Composer；他人消息左侧浅灰、本人消息右侧品牌浅蓝。`@` 改为锚定输入框上方的浮层，正文键入 `@prefix` 即筛选候选，候选选择时重新验证完整 token 替换范围，Esc/选择或点击外部后收起并回焦输入框。独立复核的四个 P2（光标中段替换、默认筛选、目录刷新、Popup 键盘关闭）均已修复。
- S19 聊天体验收口：`@` 浮层改为显式关闭（`StaysOpen=True`），由工具栏、Esc、候选选择和上下文变化管理，避免外部按钮点击打开后被 WPF 自动失焦关闭；工具栏点击立即打开，候选搜索只对实际新查询启动 200ms 防抖，保留上一批候选避免输入闪烁。候选浮层移除独立搜索框/按钮，使用方形默认头像、显示名和 `@username` 的紧凑成员行。消息改为头像与气泡分离的左右行：所有默认头像为方形、他人白色细边框气泡在左、本人品牌浅蓝气泡在右；单图继续保持比例的直接预览，圆角统一 12px。消息连续追加只在用户靠近底部时 `ScrollToEnd`，不再对新消息 `ScrollIntoView` 单个容器；同窗重发布与图片高度变化仍不重定位，真实历史 prepend 保留锚定补偿。
- 图片链路：补充 Alice/Bob 独立账户的真实 Kestrel 单 PNG 测试；双方分别以自身账号、缓存和认证下载规范消息附件，并通过既有受限解码器生成冻结缩略图。可见图片行的 UI 自动下载→缩略图触发仍由既有 `Image.Loaded` 链路持有，自动行为限定为会话已打开且图片项已物化。
- 组件化：SettingsPanelControl 与 ChatHeaderControl 已以展示 DP + RoutedEvent 形式接入；MainWindow 继续持有更新、会话、搜索、成员与生命周期协调。
- 收口：独立代码复核确认未触及 Server/Shared、消息可靠发送、附件安全或更新交接；修复窄窗口成员提示曾落入隐藏抽屉的 P2，并完成干净 HEAD 的 Release 双构建与离线校验。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S0 基线） | 0 警告、0 错误；Shared 70、Server 353、Client 1,178、Updater 38，共 1,639 项通过。 |
| `已验证` | before 快照 3/3 | 1280×720、1600×900、1920×1080 主窗口快照均存在。 |
| `已验证` | `git diff --check` | 文档差异无空白错误。 |
| `已验证` | 本地 Markdown 链接检查 | 本任务修改 Markdown 的本地相对链接均可解析且目标存在。 |
| `已验证` | Client Debug 构建 | 0 警告、0 错误（S3 后）。 |
| `已验证` | 主题、标题栏、导航、筛选与既有 UI/搜索/附件/更新定向测试 | 78 项通过（29 项 S1–S3 定向 + 49 项既有定向）。 |
| `已验证` | S4 代表性 WPF 快照 | `artifacts/rc25/ui-snapshots/after-s3/` 已生成 900×520、1280×720、1600×900、1920×1080 主聊天快照；`after-s4/` 已生成 900×520、1280×720 登录快照。已人工检查紧凑主聊天和两张登录快照，未见关键控件裁切。 |
| `已验证` | S6 after 主窗口快照 | `artifacts/rc25/ui-snapshots/after-s6/` 已生成 900×520、1280×720、1600×900、1920×1080 四档主窗口快照。 |
| `已验证` | S7/S8 after 主窗口与 Composer 快照 | `artifacts/rc25/ui-snapshots/after-s7/` 与修正后的 `after-s8/` 已生成 900×520、1280×720、1600×900、1920×1080 四档主窗口快照，以及 `composer-resized-1280x720.png`。 |
| `已验证` | `ComposerResizeThumb_*` 定向 WPF 测试 | 输入卡顶缘为 12px 高的透明 `Thumb` 热区（向上偏移 8px），鼠标为 `SizeNS`，具有 ToolTip 和自动化名称；仅正文区可在 58–200px 间扩展，900×520 下消息列表始终保留至少 120px；复杂回复/10 附件状态收缩后，回复、附件与输入操作仍在窗口边界内。 |
| `已验证` | S9–S10 次级界面及视觉收敛快照 | `after-s9/` 覆盖搜索 1600×900、设置 1280×720、强制更新 900×520、图片查看器 1280×720；`after-s10/` 覆盖新版四档主窗口和 Composer。`ClientUiSnapshotTests` 16 项及更新/搜索/附件/标题栏定向 48 项通过。 |
| `已验证` | S11 可访问性与布局复核 | `after-s11/` 覆盖 900×520 登录、900×520 消息操作、1600×900 根级搜索与 1280×720 设置。独立复核确认上述画面无 P1；定向快照/搜索/导航/附件回归 55 项通过。 |
| `已验证` | Settings/ChatHeader 控件切片 | 设置和 Header 均通过展示 DP 与 RoutedEvent 接入；Debug 构建为 0 警告/错误，控件、快照与会话定向回归 21/21、19/19 通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S12） | 0 警告、0 错误；Shared 70、Server 353、Client 1,230、Updater 38，共 1,691 项通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S12） | format、Release 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,230、Updater 38（共 1,691 项）及 `git diff --check` 均通过。 |
| `已验证` | 独立视觉与代码复核 | after-s11 视觉复核确认前序 5 个 P1 清零；代码复核确认关闭到托盘、更新交接、可靠发送、附件安全与虚拟化未回归。窄窗口成员提示的 P2 已改为顶层 `UiNoticeHost`，并有可见性回归。 |
| `已验证` | rc.25 Release 双构建与离线验证 | 在干净提交 `5f8a070c28360860acbfa6dfeace8a423fdd98c3`、SDK `10.0.110` 上向 `artifacts/rc25/release-a` 和 `release-b` 分别构建；`verify-client-release.ps1` 确认 manifest、x64/self-contained、秘密排除和 ZIP 字节一致。当前一次构建 ZIP 长度为 `165,634,787` 字节，SHA-256 为 `50B7C25BA3B503EA3C659AC07E6414A78B2F769DAF8DF19AEEDF5DD84C0D9E05`。该证据将在本任务记录提交后的最终 HEAD 重建中更新。 |
| `已验证` | S12 UI 快照定向回归 | `ClientUiSnapshotTests` 19/19 通过；`after-s12-final-draft/` 覆盖 900、1280 复杂 Composer、1600/1920 clean 和成员抽屉。独立复核确认宽屏内容宽度、设置文字和 10 附件两行均无 P1/P2。 |
| `已验证` | 双端图片 PNG Kestrel 集成 | `KestrelAttachmentDownloadIntegrationTests` 2/2 通过：Alice 发送单张 640×320 PNG，Alice 与 Bob 均从独立账户缓存下载逐字节相同的附件，并安全生成 320×160 冻结缩略图。 |
| `已验证` | S12 Full / Release | Fast 与 Full 均已在 S12 工作树运行；最终双构建仍必须从新的干净提交运行。真实 Windows 100%/125%/150% DPI、托盘及强制更新交接仍需人工矩阵。 |
| `已验证` | S13 WPF 快照矩阵 | `ClientUiSnapshotTests` 24/24 通过；`after-s13-aspect-final/` 覆盖登录、搜索、设置、成员、强制更新、查看器、主窗口及横图/方图/竖图直接预览。人工复核确认成员与设置均为覆盖层，无第四列。 |
| `已验证` | S13 图片呈现与未开放入口 | 图片呈现回归及快照 34/34 通过；横/方/竖图保持比例并不超过 360×280。导航轨键盘/UIA 与连续未开放入口计时/零业务副作用回归已覆盖。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S13） | 0 警告、0 错误；Shared 70、Server 353、Client 1,235、Updater 38，共 1,696 项通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S13） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,240、Updater 38（共 1,701 项）及 `git diff --check` 均通过。 |
| `已验证` | 发布校验器秘密排除回归 | `ClientReleasePackageTests` 1/1 通过（真实双包和 ZIP 篡改），离线 verifier 精确拒绝 `credentials.bin`、`auth-access_token.bin`。 |
| `已验证` | S14 组件与次级界面定向回归 | 组件提取后的 UI/会话/Composer/附件/搜索 537/537、直接图片预览 13/13、附件下载呈现 19/19 通过；标题栏与设置控件 8/8、可选更新交接 7/7 通过。 |
| `已验证` | S14 快照与视觉复核 | `ClientUiSnapshotTests` 26/26 通过；`after-s14-secondary/` 覆盖登录、搜索稳定空态、成员覆盖抽屉、设置与可选更新、强制更新、查看器及全部主窗口尺寸。人工复核确认成员不再形成第四列，搜索不再坍缩。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S14） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,246、Updater 38，共 1,707 项通过。日志：`artifacts/rc25/verify-s14-fast.log`。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S14） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,246、Updater 38（共 1,707 项）及 `git diff --check` 均通过。日志：`artifacts/rc25/verify-s14-full-r2.log`。 |
| `已验证` | S15 次级界面回归 | `ClientUiSnapshotTests` 26/26、消息控件/主题/设置定向 11/11 均通过；覆盖登录、搜索、成员、设置/可选更新、强制更新、查看器、消息动作和输入卡片。快照：`artifacts/rc25/ui-snapshots/after-s15-secondary/`。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S15） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,249、Updater 38，共 1,710 项通过。S15 Full、最终双构建和真实 Windows 人工矩阵仍未验证。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S15） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,249、Updater 38（共 1,710 项）及 `git diff --check` 均通过。最终双构建和真实 Windows 人工矩阵仍未验证。 |
| `已验证` | S16 次级界面快照与定向回归 | `ClientUiSnapshotTests`、更新交接、搜索呈现及成员展示定向回归 50/50 通过；`after-s16-secondary/` 新增查看器加载/失败画面，并重新覆盖登录、搜索、成员、设置/可选更新、强制更新、查看器、主窗口与图片预览。人工复核确认查看器两种空态的状态、说明和关闭动作完整可见。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S16） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,254、Updater 38，共 1,715 项通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S16） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,254、Updater 38（共 1,715 项）及 `git diff --check` 均通过。最终双构建和真实 Windows 人工矩阵仍未验证。 |
| `已验证` | S17 成员、提及与消息视口定向回归 | 提及候选的“已有 token / 新插入 token”两条成功路径均会关闭候选面板并恢复输入焦点；消息选中外观保持透明；同窗口重绘不再触发错误的 prepend 滚动补偿。定向 16/16 通过；WPF 快照 28/28 通过，见 `artifacts/rc25/ui-snapshots/after-s17-interaction-fixes/`。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S17） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,257、Updater 38，共 1,718 项通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S17） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,257、Updater 38（共 1,718 项）及 `git diff --check` 均通过。 |
| `已验证` | S18 聊天体验定向回归 | 首次选择、`@` 片段替换、Popup 收起、双向气泡与 bootstrap `general` 的定向测试通过；`ClientUiSnapshotTests` 28/28 通过。快照：`artifacts/rc25/ui-snapshots/after-s18-chat-experience-r2/`。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S18） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,264、Updater 38，共 1,725 项通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S18，复核修正后） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,265、Updater 38（共 1,726 项）及 `git diff --check` 均通过。 |
| `已验证` | S19 聊天定向回归 | 消息滚动策略、消息视觉、真实 `@` 按钮开关与候选选择共 18/18 通过。 |
| `已验证` | S19 WPF 快照 | `ClientUiSnapshotTests` 28/28 通过；`artifacts/rc25/ui-snapshots/after-s19-chat-polish/` 覆盖主窗口、登录、成员、设置、搜索、强制更新、查看器和横/方/竖单图直接预览。已人工检查 1920×1080 主聊天与 1280×720 单图预览：短历史贴近 Composer，方形头像与气泡分离，图片无文件卡片外框。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast`（S19） | Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,266、Updater 38，共 1,727 项通过。首轮 Server 发布脚本安全断言受并行临时文件时序影响失败；单独重跑及最终 Fast 均通过。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full`（S19） | format、Release 构建 0 警告/错误、Release 全量 Shared 70、Server 353、Client 1,266、Updater 38（共 1,727 项）及 `git diff --check` 均通过。 |

### 文件范围

- 新增：上述文档、Client 资源/展示类型/控件及 S1–S3 测试。
- 修改：`docs/ai/RC25_EXECUTION_PLAN.md`、`docs/ui-design-guidelines.md`、`RelayCove_工程落地方案.md`、`App.xaml`、`MainWindow.xaml(.cs)`、控件 XAML、发布 verifier 与其包装测试。
- 删除：无

### 决策与限制

- 决策：v1.1 是 rc.25 唯一可执行规格；v1.0 为保留的历史设计输入。品牌主色固定为 `#1677D2`，绿色仅表达成功或在线等真实语义。
- 已知限制：S19 的聊天体验、快照、Fast 与 Full 已完成，最终双构建仍必须从新的干净提交运行。`general` 仅在启用 bootstrap 的全新空库中创建，不接管已有数据库或自动复建被删除频道。真实 Windows 100%/125%/150% DPI、拖动/边缘缩放/Alt+Space、托盘恢复/真正退出及强制更新交接尚未在人工桌面矩阵验证，必须保持 `未验证`，Windows 原生窗口行为不能仅由 `RenderTargetBitmap` 验收。图片自动下载仅在会话已打开且图片项实际可见时启动，不进行后台全量图片下载。

### 下一步

- 从包含 S16 与本任务记录的干净精确 HEAD 执行 Release 双构建与离线校验；随后由独立只读复核确认组件不接管业务所有权。真实 Windows DPI、窗口/托盘与强制更新交接人工矩阵保持后续 `未验证` 项。
