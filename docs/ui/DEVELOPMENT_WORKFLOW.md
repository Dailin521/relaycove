# RelayCove UI 开发工作流

状态：Active
最后更新：2026-08-24

自 Stage 25 起，RelayCove.App MAUI 是唯一继续开发和交付的产品客户端；正式 RelayCove.Web、下文 Web-first 流水线和冻结 `chat-ui-v1` 只保留历史证据，不再构成当前对齐门禁。当前 MAUI UI 工作采用：明确单一问题 → 更新交互合同 → 原生 XAML/ViewModel/Windows adapter 实现 → 最小确定性验证 → 用户 Visual Studio 人工验收 → 文档收尾 → 最小提交推送。现有 Zulip 官方 Web 保留不动。

## 1. 标准流水线

```text
需求与范围确认
  -> RelayCove.Web 正式组件与 fake 数据边界
  -> typecheck / unit / production build / Playwright
  -> 日常本地正式客户端/真实 Realm 审查；fixture 仅自动化；需要时显式部署大版本验收入口
  -> 用户逐页审查与 Web 验收
  -> 冻结版本、截图、Token、功能矩阵和场景
  -> 建立 Stage 22M task slice
  -> 原生 XAML + ViewModel 实现
  -> 自动化验证
  -> Windows 真实窗口视觉/键盘验收
  -> 更新状态与新基线
```

不得跳过正式 Web 验收直接把临时 HTML 行为翻译进 XAML。`chat-ui-v1` 冻结目录和 SHA-256 永不原地覆盖；后续变更在生产 Web 和新版本记录中完成。

## 2. 阶段 A：RelayCove.Web 正式实现

### 输入

- 明确的用户目标、平台和目标视口。
- 当前产品范围与服务器能力。
- 已有品牌资产和已确认参考风格。

### 产物

- `src/RelayCove.Web/` 下可独立部署的 TypeScript/React/Vite 工程。
- 窗口、导航、会话、聊天、Composer、详情和设置等按状态边界组件化。
- 设计 Token 独立维护，图标和依赖进入构建产物，不依赖运行时 CDN。
- 在 1440×900 主视口检查；需要时补充 1024×768、浅色和深色。若只截取应用元素，清单同时记录浏览器视口与 PNG 实际像素尺寸。
- 可操作的正常、加载、离线、空、发送中、失败等状态。

### 规则

- 确定性演示数据只存在开发 fixture，并从生产构建排除；正式 Zulip API/session 路径不得导入 fixture。
- 未接入能力必须隐藏、禁用或标为“后续能力”，不能把可点击外壳误报为生产能力。
- 使用与生产相同的术语：频道、话题、私信、未读、Waiting、WaitExpired、Failed。
- 不用时间、静态红点或硬编码权限掩盖真实状态来源。
- 每个重要动作至少定义成功、取消、失败和无权限结果。
- Web 可按确认策略默认记住 API Key，但 Key 不进入 URL、日志、UI、异常或测试快照；注销清除 local/session storage。
- Web 与 MAUI 都直接连接 Zulip，不新增 RelayCove server、BFF 或代理协议。

## 3. 阶段 B：审查与版本冻结

用户明确确认后才冻结新的 Web 交互版本。冻结动作包括：

1. 完成已提出的收尾项。
2. 在生产构建上验证关键交互、控制台、键盘和响应式布局。
3. 将版本说明、PNG 和 SHA-256 保存到新目录；若保留审查 HTML，也只能作为证据而非生产入口：

   `docs/ui/baselines/<baseline-id>/`

4. 写入基线清单、视口、主题、已验证行为和限制。
5. 把交互版本标为 Frozen；后续禁止原地覆盖。冻结的是验收输入，不冻结两端 UI runtime。

建议基线 ID 使用功能和递增版本，例如 `chat-ui-v1`、`login-ui-v1`、`chat-ui-v2`。

## 4. 阶段 C：交互文档

每个冻结基线必须有可独立实现的规格，至少包含：

- 信息架构和 Zulip 领域映射；
- 每个入口的触发条件、状态变化、副作用、取消和失败；
- loading/empty/offline/locked/outbox 状态矩阵；
- 搜索字段和明确排除字段；
- 权限 capability 矩阵和危险确认；
- 键盘、焦点、无障碍和窗口收窄规则；
- Given/When/Then 验收场景；
- fixture/外壳行为与正式 Zulip 数据行为的已知差异；
- Stage 22W、Stage 22M、Stage 21 外部门禁和后续能力门的归属。

当截图与规格冲突时，先由用户确认预期，再修改规格和新版本；不得由 MAUI 实现者自行猜测。

## 5. 阶段 D：Web 工程和安全边界

- 浏览器 HTTP adapter 与 React 组件分离，所有测试注入 fake/mocked transport。
- Realm 只接受规范 HTTPS origin；`/server_settings` 无凭据，密码只进入 `/fetch_api_key` form body，后续请求使用邮箱 + API Key 的 HTTP Basic。
- NuGet assets、npm 依赖和 Chromium 由单独 bootstrap 显式预置；普通 Fast/Full 只使用 `--no-restore` .NET 命令，不恢复/安装依赖、不下载浏览器、不访问外部网络、不使用真实凭据。
- 生产构建必须排除 fixture 数据与运行时 CDN；E2E 专用构建才允许加载集中 fixture。
- 浏览器同源/CORS 和正式静态托管安全响应头属于部署门禁；不以新增代理来绕过。

### 5.1 本地与大版本验收节奏

- 日常开发双击仓库根目录 `start-web-dev.cmd`：只启动本机 `npm run dev`，等待就绪后打开 `http://127.0.0.1:5173/` 正式登录并读取真实 Realm；关闭控制台即结束本地服务。真实写入必须服从当轮明确授权与目标范围。
- fixture 不再是日常人工验收入口，只允许通过显式 `--mode fixture`/E2E 模式运行，且始终从 production bundle 排除。
- 只有需要服务器人工验收的大版本才双击 `deploy-web.cmd`。它先跑完整 Web 验证，再只上传 `dist/`，校验 SHA-256/归档路径，写入新 release 并原子切换 `current`；不做 deploy-on-save 或自动清理旧 release。
- 固定入口是同 Realm 的 `https://hklight.2000521.xyz/relaycove-web/`。官方 Zulip `/` 与旧 `/relaycove/` 不改；静态托管不代理 Zulip API，不构成 RelayCove server/BFF。
- 服务器登录壳、fixture 或本地真实读写各自只证明对应边界；均不能替代 MAUI 窗口或 Stage 21 两账号 Live/人工登录/干净 VM 门禁。

## 6. 阶段 E：Stage 22M MAUI 原生复刻

### 6.1 架构边界

- 只使用原生 MAUI XAML、控件、ResourceDictionary、Behavior 和 Windows 平台适配；禁止 WebView 承载原型。
- ViewModel 只依赖 `IClientSession` 或 App 层 UI 服务，不直接调用 HTTP、SQLite 或 Zulip DTO。
- code-behind 只处理焦点、滚动、指针、窗口和 View 生命周期。
- 数据库和网络 I/O 不在 UI 线程执行。
- `CollectionView` 保持虚拟化，旧请求在会话切换时取消。

### 6.2 映射规则

| 共享 Web 合同 | MAUI 落点 |
|---|---|
| CSS 颜色、间距、字号、圆角 | `ResourceDictionary` token，不散落魔法值 |
| 重复 HTML 元素 | ContentView、DataTemplate 或 Style |
| JS UI 状态 | ViewModel 属性、命令或纯投影器 |
| 指针/键盘细节 | Behavior、GestureRecognizer 或 Windows adapter |
| 弹层 | 原生 Popup/ContentView overlay，并管理焦点返回 |
| 响应式布局 | VisualStateManager/窗口尺寸触发器 |
| 图标 | 仓库许可清晰的本地矢量资源，不依赖运行时 CDN |

### 6.3 组件边界

聊天主界面优先拆为：

- `NavigationRailView`
- `ConversationPaneView`
- `ChatHeaderView`
- `MessageListView`
- `ComposerView`
- `DetailsPaneView`
- `SuggestionListView`
- `OverlayHostView`

拆分以状态归属和可测试行为为依据，不为每个视觉小块创建公共 API。

### 6.4 投影与更新

- 使用 `ConversationKey`、message ID 和 user ID 做 keyed reconcile，避免每次 `Clear + Add` 导致选择、滚动和焦点丢失。
- 会话搜索先投影本机连续部分匹配，再以 300 ms debounce 调用只读服务器搜索；所有响应必须携带查询 generation 与账号边界，分页按 message ID 合并且不得把同一会话的不同命中压成一行，旧查询/旧账号/取消响应失败关闭。历史命中只携带瞬时 message ID，不写入领域或缓存。
- 未读数只从 Core `UnreadState` 投影；服务器确认成功前不乐观清零。
- Core 历史加载不得自动标读。当前会话只有在 RelayCove HWND 是真实非最小化前台窗口、聊天面板可见、无模态遮罩、history generation 仍匹配且原生列表确认到达底部后，App 才能自动标读；生命周期激活需要延后复核真实前台，任务栏悬停必须两次失败关闭。请求携带 expected conversation，切换会话后必须 fail closed。
- Windows 通知头像不得直接使用需要认证的 Realm URL；先走同 Realm 受控媒体读取，以不透明账号目录和账号/URL 哈希命名本机 PNG/JPEG 缓存，再把 file URI 交给通知 Builder；成功注销和清本机缓存必须删除该账号目录。任务栏数字在未打包窗口上必须以当前 HWND 可见结果为准，WinAppSDK identity badge 不可见时使用 `ITaskbarList3` overlay 投影同一权威数量。
- Composer 的当前 Windows 产品规则是 Enter 发送、Ctrl+Enter 换行；Windows adapter/handler 必须保护 IME 组合输入，并与可见提示和无障碍说明保持一致。
- Windows Composer 要求插入光标在焦点保持期间持续按系统周期闪烁，但不得修改系统全局 caret timeout、注册表或用户闪烁速率。优先保留平台控件自己的 caret；不得叠加第二条自绘光标或用背景遮罩覆盖系统光标。只有在复现的平台缺陷无法用原生控件修复时，才另立任务评估自绘替代。
- 光标变更必须分别验证首次点击空文本、CJK 文本开头/中间/末尾、换行、选区、切换会话后再次聚焦、发送清空后继续输入、IME 组合和超过系统 timeout 的持续闪烁。编译或初始两三次闪烁不能替代真实窗口验收。
- 草稿、输入区高度和详情开关属于 App/设备状态，不进入 Core 或 SQLite 业务表。
- 频道管理按 capability 控制可见性和命令，提交时仍处理 403。
- Web fixture 占位数据不自动映射成生产数据；成员关系、共同频道、presence、saved flags 或 capability 缺少契约时两端均隐藏/标为不可用。

### 6.5 原生 UI 快速预览循环

- 完整操作与排障基线见 [MAUI 原生 UI 快速预览手册](MAUI_PREVIEW_WORKFLOW.md)。当前工具链显式锁定 .NET SDK `10.0.400`、MAUI `10.0.20`、Windows `win-x64`；Visual Studio 更新后先核对 SDK/workload，再调整仓库版本，并为新 SDK/MAUI/RID 显式准备一次依赖。不能删除版本门禁来规避错误。
- 使用 MAUI-aware 的 `Windows Machine` 启动配置和 Debug 会话；该配置只额外设置 `RELAYCOVE_NATIVE_UI_PREVIEW=1`，继续使用不能联网的内存 preview session。不要用固定 exe 路径创建自定义 profile。
- Visual Studio 内保存的 XAML 优先走 XAML Hot Reload、Live Visual Tree；Codex 或其他外部工具修改 XAML 时不能假定 VS 会接管。当前 `dotnet watch` 也不监听原始 XAML，外部修改应先成批完成，再运行 App-only `dotnet build ... --no-restore` 并只重启一次预览。
- C# 类型、Behavior、新资源项、DI、项目文件、编译绑定和窗口启动逻辑必须增量编译/重启。编译失败时保留旧预览，不得先误杀其他 worktree 的同名进程。
- Debug preview 每次启动自动选择非主显示器，无副屏时安全回退；生产窗口不强制移动。副屏定位按目标 monitor 的物理缩放换算 DIP，不能使用移动前的窗口 DPI。
- 固定视觉状态使用 `start-maui-preview.ps1 -Scene/-Theme/-Width/-Height` 直接注入 Debug ViewModel；截图使用记录 PID/EXE 的 `capture-maui-preview.ps1` 和 `PrintWindow`。不得用鼠标移动、系统点击、键盘注入或依赖桌面前台焦点打开弹层。
- 启动器先构建到唯一的忽略目录，成功后才替换自己记录的旧进程，并以 EXE 输出目录作为 WinUI 工作目录；捕获器等待副屏 DPI 与启动定位稳定后再按 DWM 边界调整目标尺寸。
- 一个组件或响应式状态完成后才运行最窄 App/ViewModel 测试并保留一张证据图；一个完整 Slice 完成后才运行 Fast。Release/Full、全视口截图和独立复核仍留在提交门禁。
- 不得为预览速度绕过 XAML 编译、切换到 WebView、把 fixture 接入生产会话，或用预览截图替代真实 Realm、安装包和干净 VM 验收。

## 7. 新能力门

以下视觉入口不能只靠 React 或 XAML 实现，必须单独立项并在两端功能矩阵中标记：

### 成员关系与已保存读取

- 明确 Realm 用户、频道成员、共同频道和 presence 的不同数据来源，禁止互相推断。
- 为 saved/starred flags 定义 Core、Zulip.Client、Data 和撤权清理规则；能力启用前隐藏“已保存”结果区。
- 频道成员读取是 `@` 候选和频道管理的前置能力，但只读能力本身不授权任何管理写入。

### 搜索

- Core：查询、结果、来源和取消语义。
- Data：账号隔离的缓存搜索与索引。
- Zulip.Client：在线 narrow/search 映射。
- App：分组、绿色高亮、键盘和过期请求抑制。

### 附件

- Stage 22W 已实现 Web 的任意文件多选/拖放、分会话草稿、取消、图片遮罩预览、普通文件卡片、multipart 顺序上传、临时授权 Blob 下载、重定向禁用、脱敏和显式一次发送语义。
- Web 受控媒体必须限制同 Realm 路径、类型、单文件/每消息/并发/总缓存资源，并在 logout/unmount/release 时 abort 与 revoke；超额链接保留 raw Markdown。
- 只有 PNG/JPEG/WebP/GIF/AVIF 可内嵌预览；SVG/HTML/PDF/Office/压缩包及未知类型只能作为文件下载，不得进入 `img`、`iframe`、`object` 或活动 HTML。
- Stage 22M 仍须在原生 App/Core/Zulip.Client/Data 边界另行复刻和验收，不共享 Web 运行时代码。
- 若任一端持久缓存图片，必须另行处理大小上限、账号隔离和撤权清理；当前 Web Blob 只存在页面内存。

### `@` 成员

- 先决定候选是频道成员还是 Realm 活跃用户，并保证数据源真实。
- 增加光标解析、匹配、插入和 Zulip Markdown 格式测试。
- DM 不自动弹候选。

### 频道管理

- Stage 22W 已实现当前用户主动退订：按 reducer 中的真实订阅名调用 Zulip unsubscribe，成功后复用 subscription removal 清理；结果未知不自动重试。测试不得退出真实业务频道。
- 频道设置必须把当前设置目标与当前聊天会话分离，所有异步读取和写入捕获频道 ID、generation 与分页身份；关闭、换频道或换分页后旧响应不得回填。
- Personal、成员添加、成员移除、频道管理和组织管理员能力分别投影，不能用单一管理员布尔值替代。未知/缺失/停用/循环 group-setting 一律失败关闭。
- 订阅者管理必须把权威频道成员 ID 与权威组织用户目录作为同一代读取；任何一侧失败、成员 ID 无法映射或分页已切换时不得显示部分列表，也不得启用写入口。添加/移除若协议只接受频道名称，写前必须按频道 ID 重取权威名称，且非幂等写不重试。
- 设置快照中的当前用户订阅状态必须由同代 `GET /users/me/subscriptions` 与 `/streams` 按频道 ID 严格对账；本地事件缓存只能补充显示数据，不能把服务端确认的订阅状态改回未订阅。根字段、ID、重复项或跨响应引用异常必须失败关闭。
- 个人频道颜色使用本地草稿实时预览；页面常驻入口、官方 4×6 色盘和自定义 Hex 输入不得产生外部写入，只有格式合法并由用户在颜色子层显式确认时才提交。外点、`Escape` 和取消必须回滚草稿并恢复入口焦点。
- 命名 group-setting 更新携带精确 old/new；匿名组在没有完整编辑器时只读。个人通知的继承值只能读取，除非官方 API 明确支持 reset，否则不得发送猜测的 `null`。
- 协议写入需要独立只读复核、403 处理和危险确认；非幂等请求不自动重试，成功后再读取权威状态。
- 不把服务器“归档/停用”包装成未经证实的永久删除。

## 8. 验证门禁

### 开发中

1. Web 运行 typecheck、unit、production build 和最窄 Playwright；MAUI 运行最窄 App/ViewModel 测试。
2. 运行 `pwsh ./scripts/verify.ps1 -Mode Fast`。
3. Web 对 UI 变更完成 1440×900 浅/深色与 1024×768 浅色截图，检查 console error/warning、键盘和横向溢出。
4. MAUI 另行检查键盘、焦点、200% 缩放、长文本、空列表和滚动锚点。

### 交付前

1. 运行 `pwsh ./scripts/verify.ps1 -Mode Full`。
2. 认证、协议、同步、数据、outbox 或打包变更完成独立只读复核。
3. 22W 在固定 Chromium/生产构建完成浏览器验收；22M 在真实 Windows 窗口验证标题栏、拖拽、FilePicker、遮罩和长列表。
4. 更新 `docs/ai/STATUS.md` 和对应 task；未执行的 VM/Live/MAUI 视觉门禁保持未验证。

浏览器截图、Web build、MAUI build、单元测试、Windows 人工验收、Live 和干净 VM 验收是不同证据，不能互相替代。

## 9. 完成定义

一个 UI 切片只有在以下条件全部满足时才完成：

- 22W 的正式 Web、交互规格和版本证据一致，fixture 与正式数据路径分离；
- 22M 只在交互版本冻结后启动，MAUI 行为与规格一致，差异有明确批准记录；
- 无 WebView、无新增服务端/BFF、无 UI 线程 I/O、无越层依赖；
- 状态、权限、失败和取消路径有确定性测试；
- Fast/Full 通过；
- 22W 浏览器验收和 22M Windows 真实窗口验收分别完成并报告；
- STATUS 只报告实际证据，正式消息同步、后续能力和 Stage 21 外部门禁仍明确列出。
