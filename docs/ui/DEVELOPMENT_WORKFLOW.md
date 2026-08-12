# RelayCove UI 开发工作流

状态：Active
最后更新：2026-08-12

后续 UI 工作固定采用：**先 Web UI，再冻结交互文档，最后转换为原生 MAUI**。这个顺序用于降低视觉返工，但不会绕过产品范围、协议、安全和真实 Windows 验收。

## 1. 标准流水线

```text
需求与范围确认
  -> Web UI 交互原型
  -> 用户逐页审查
  -> 冻结基线和截图
  -> 交互规格/状态/权限/验收文档
  -> 建立 MAUI task slice
  -> 原生 XAML + ViewModel 实现
  -> 自动化验证
  -> Windows 真实窗口视觉/键盘验收
  -> 更新状态与新基线
```

不得跳过文档直接把临时 HTML 行为翻译进 XAML。若只修改文字、间距等不改变行为的小改动，也必须更新基线版本或在当前未冻结草案中完成，不能覆盖已冻结目录。

## 2. 阶段 A：Web UI 原型

### 输入

- 明确的用户目标、平台和目标视口。
- 当前产品范围与服务器能力。
- 已有品牌资产和已确认参考风格。

### 产物

- 可交互独立 HTML。
- 在 1440×900 主视口检查；需要时补充 1024×768、浅色和深色。若只截取应用元素，清单同时记录浏览器视口与 PNG 实际像素尺寸。
- 可操作的正常、加载、离线、空、发送中、失败等状态。

### 规则

- 原型可以模拟尚未接入的能力，但必须在 UI 或规格中标为“原型/后续能力”。
- 使用与生产相同的术语：频道、话题、私信、未读、Waiting、WaitExpired、Failed。
- 不用时间、静态红点或硬编码权限掩盖真实状态来源。
- 每个重要动作至少定义成功、取消、失败和无权限结果。

## 3. 阶段 B：审查与冻结

用户明确确认后才冻结。冻结动作包括：

1. 完成已提出的收尾项。
2. 在真实浏览器验证关键交互和控制台。
3. 将独立 HTML、PNG 和 SHA-256 保存到新目录：

   `docs/ui/baselines/<baseline-id>/`

4. 写入基线清单、视口、主题、已验证行为和限制。
5. 把状态标为 Frozen；后续禁止原地覆盖。

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
- 原型行为与生产行为的已知差异；
- Stage 21、当前 MAUI 转换阶段和后续能力门的归属。

当截图与规格冲突时，先由用户确认预期，再修改规格和新草案；不得由 MAUI 实现者自行猜测。

## 5. 阶段 D：MAUI 转换

### 5.1 架构边界

- 只使用原生 MAUI XAML、控件、ResourceDictionary、Behavior 和 Windows 平台适配；禁止 WebView 承载原型。
- ViewModel 只依赖 `IClientSession` 或 App 层 UI 服务，不直接调用 HTTP、SQLite 或 Zulip DTO。
- code-behind 只处理焦点、滚动、指针、窗口和 View 生命周期。
- 数据库和网络 I/O 不在 UI 线程执行。
- `CollectionView` 保持虚拟化，旧请求在会话切换时取消。

### 5.2 映射规则

| Web 产物 | MAUI 落点 |
|---|---|
| CSS 颜色、间距、字号、圆角 | `ResourceDictionary` token，不散落魔法值 |
| 重复 HTML 元素 | ContentView、DataTemplate 或 Style |
| JS UI 状态 | ViewModel 属性、命令或纯投影器 |
| 指针/键盘细节 | Behavior、GestureRecognizer 或 Windows adapter |
| 弹层 | 原生 Popup/ContentView overlay，并管理焦点返回 |
| 响应式布局 | VisualStateManager/窗口尺寸触发器 |
| 图标 | 仓库许可清晰的本地矢量资源，不依赖运行时 CDN |

### 5.3 组件边界

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

### 5.4 投影与更新

- 使用 `ConversationKey`、message ID 和 user ID 做 keyed reconcile，避免每次 `Clear + Add` 导致选择、滚动和焦点丢失。
- 未读数只从 Core `UnreadState` 投影；服务器确认成功前不乐观清零。
- 草稿、输入区高度和详情开关属于 App/设备状态，不进入 Core 或 SQLite 业务表。
- 频道管理按 capability 控制可见性和命令，提交时仍处理 403。
- 原型占位数据不自动映射成生产数据；成员关系、共同频道、presence、saved flags 或 capability 缺少契约时隐藏/标为不可用。

## 6. 新能力门

以下原型能力不能只靠 XAML 实现，必须单独立项：

### 成员关系与已保存读取

- 明确 Realm 用户、频道成员、共同频道和 presence 的不同数据来源，禁止互相推断。
- 为 saved/starred flags 定义 Core、Zulip.Client、Data 和撤权清理规则；能力启用前隐藏“已保存”结果区。
- 频道成员读取是 `@` 候选和频道管理的前置能力，但只读能力本身不授权任何管理写入。

### 搜索

- Core：查询、结果、来源和取消语义。
- Data：账号隔离的缓存搜索与索引。
- Zulip.Client：在线 narrow/search 映射。
- App：分组、绿色高亮、键盘和过期请求抑制。

### 图片附件

- App：FilePicker、预览、取消和大图遮罩。
- Core/Session：上传结果与发送一次语义。
- Zulip.Client：multipart 上传、授权下载、重定向禁用和脱敏。
- Data：若缓存图片，必须处理大小上限、账号隔离和撤权清理。

### `@` 成员

- 先决定候选是频道成员还是 Realm 活跃用户，并保证数据源真实。
- 增加光标解析、匹配、插入和 Zulip Markdown 格式测试。
- DM 不自动弹候选。

### 频道管理

- 建模 `CanEditChannel`、`CanManageMembers`、`CanArchiveChannel` 等服务器能力。
- 协议写入需要独立只读复核、403 处理和危险确认。
- 不把服务器“归档/停用”包装成未经证实的永久删除。

## 7. 验证门禁

### 开发中

1. 运行最窄 App/ViewModel 测试。
2. 运行 `pwsh ./scripts/verify.ps1 -Mode Fast`。
3. 对 UI 变更完成 1440×900 和 1024×768 的浅色/深色截图比较。
4. 检查键盘、焦点、200% 缩放、长文本、空列表和滚动锚点。

### 交付前

1. 运行 `pwsh ./scripts/verify.ps1 -Mode Full`。
2. 需要协议、同步、数据、outbox 或打包变更时完成独立只读复核。
3. 在真实 Windows 窗口验证标题栏、拖拽、FilePicker、遮罩和长列表。
4. 更新 `docs/ai/STATUS.md` 和对应 task；未执行的 VM/Live/视觉门禁保持未验证。

浏览器截图、MAUI build、单元测试、Windows 人工验收和干净 VM 验收是不同证据，不能互相替代。

## 8. 完成定义

一个 UI 切片只有在以下条件全部满足时才完成：

- Web 原型和交互规格已由用户确认并冻结；
- MAUI 行为与规格一致，差异有明确批准记录；
- 无 WebView、无 UI 线程 I/O、无越层依赖；
- 状态、权限、失败和取消路径有确定性测试；
- Fast/Full 通过；
- Windows 真实窗口完成目标视口、主题、键盘和缩放验收；
- STATUS 只报告实际证据，后续能力和外部门禁仍明确列出。
