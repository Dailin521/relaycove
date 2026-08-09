# RelayCove UI Acceptance Checklist v1.1

> rc.25 验收以此清单和 [`RelayCove_UI_Redesign_Implementation_Spec_v1.1.md`](RelayCove_UI_Redesign_Implementation_Spec_v1.1.md) 为准。v1.0 保留为历史输入，不能作为验收口径。

## 1. S0 基线与范围

- [x] 基准为 `baaae88`；Fast 为 0 警告、0 错误，Shared 70 + Server 353 + Client 1,178 + Updater 38 = 1,639 项。
- [x] before 快照 3/3：1280×720、1600×900、1920×1080 主窗口。
- [x] v1.0 未改；v1.1 冻结蓝白 token、断点、标题栏、功能映射与本清单。
- [x] Fast、Full、Release 构建和 after 快照自动化验证；真实 Windows 原生验收仍保持 `未验证`。

## 2. 布局与窗口

- [ ] 48px 自绘标题栏使用 `WindowChrome`；拖动、双击、最小化、最大化/还原、Alt+Space 和边缘缩放正常。
- [ ] 关闭仅进入既有 `Window.Close()` → 托盘隐藏流程；真正退出和更新交接未被绕过。
- [ ] `>=1400` 为 72px Rail + 340px Conversation + 可打开 360px 成员抽屉。
- [ ] `1100–1399` 为 72px Rail + 320px Conversation，抽屉关闭。
- [ ] `900–1099` 为 64px Rail + 280px Conversation，抽屉关闭；Chat 和 Composer 仍可用。
- [ ] 900×520 最小窗口、1280×720、1600×900、1920×1080 无关键裁切、重叠或固定聊天宽度。

## 3. 功能映射与不可伪造状态

| 验收项 | 通过条件 |
| --- | --- |
| Rail | 头像/设置、聊天 All、频道 Channels 可用；联系人、通知、文件、更多只显示无副作用提示。 |
| Header | 成员和显式搜索复用既有功能；置顶、会话通知、更多只提示。 |
| Message / Composer | 回复、复制、条件式重试、附件、`@`、正文、发送保留；Emoji、语音、截图、下拉及消息扩展操作只提示。 |
| 提示可达性 | 未开放入口不禁用，具有 ToolTip/自动化名称；约三秒非模态提示，连续点击重置计时。 |
| 权威状态 | 只表达 `Sending`、`Sent`、`Failed`；不出现 `Delivered`、`Read`、`Deleted`、`Retrying` 或伪造 Presence/角色/收藏/置顶。 |

## 4. 行为、可访问性与回归

- [ ] 本地会话搜索为 Name/Preview 的 `OrdinalIgnoreCase` 包含匹配；All/Unread/Channels/Direct 不改变未读、会话或权威选择。
- [ ] 会话和消息列表仍使用虚拟化；连续消息、日期/新消息线、回复、提及、链接、图片和文件卡片不回归。
- [x] Composer 保留多行、Enter、Ctrl+Enter、附件选择/拖入/粘贴、`@`、单附件移除和成功清理语义。
- [ ] 图片继续走授权、缓存、完整性校验和安全解码，绝不直接绑定远端 URL。
- [ ] 键盘焦点、Tab/Enter/Space/Escape、AutomationProperties 与焦点回退可用。

## 5. 快照、验证与发布门

- [x] after 快照：登录（900×520、1280×720）、主聊天（900×520、1280×720、1600×900 抽屉、1920×1080）、Composer 压力、搜索稳定空态、成员抽屉、设置/可选更新、强制更新、图片查看器；证据在 `artifacts/rc25/ui-snapshots/after-s14-secondary/`。
- [ ] 标准宽度布局误差不超过 4px；WPF/DPI 字体差异仅人工复核，不做跨 DPI 逐像素断言。
- [x] Fast、Full、Release 0 警告、0 错误；既有快照与定向回归通过；`git diff --check` 通过。
- [ ] 独立只读复核确认未改变关闭到托盘/更新交接、Dispatcher/取消、虚拟化/焦点、未开放副作用或 Shared/Server/数据库/可靠发送语义。
- [ ] 真实 Windows 在 100%/125%/150% DPI 完成标题栏、托盘恢复、真正退出和强制更新交接验证；未执行项明确为 `未验证`。
- [ ] 仅在干净、已提交 HEAD 双构建 rc.25，两个 ZIP 字节一致并通过离线包验证；不生成线上 manifest、不推送或部署。

## 6. Given / When / Then 验收场景与证据

| 场景 | Given | When | Then | 自动化/人工证据位置 |
| --- | --- | --- | --- | --- |
| 主题与资源 | Client 启动并合并资源字典 | 解析主题、图标和控件模板 | 蓝白 token 可用，图标为矢量资源，焦点可见 | `ClientThemeResourceTests`、`ClientWindowChromePresentationTests` |
| 标题栏 | 实际 WPF 窗口在 100%、125%、150% DPI | 拖动、双击、最小化、最大化、Alt+Space、关闭 | 原生窗口行为正常；关闭仍交给托盘/更新生命周期 | 自动化标题栏测试；人工记录写入 `docs/ai/tasks/2026-08-09-stage-20-rc25-ui-redesign.md` |
| 导航与筛选 | 已加载真实会话摘要 | 选择 Rail、输入本地查询或切换筛选 | 仅本地按 Name/Preview 筛选；选择和未读不被改变 | `ClientConversationFilterPolicyTests`、`ClientNavigationRailPresentationTests` |
| 未开放入口 | 可点击的弱化入口已显示 | 连续点击联系人、Emoji 等入口 | 精确 FeatureId 触发约三秒提示，无网络/持久化/导航副作用 | `ClientConversationPanelPresentationTests` 与功能映射测试 |
| Composer | 已选择会话，含普通、回复和 10 附件状态 | 输入、发送、拖入/粘贴、@、上沿拖动输入卡 | 既有发送与附件语义不变；只拉伸正文，工具栏固定，消息流保留可视空间 | `ClientUiSnapshotTests.ComposerResizeThumb_*`、附件与发送回归 |
| 覆盖层 | 主聊天、成员、设置、搜索、图片或强制更新处于可显示状态 | 打开/关闭覆盖层及 Escape | 焦点在最前层，成员/设置不形成第四列；图片仍使用安全解码；更新交接不变 | 覆盖层定向测试、after 快照、人工焦点检查 |
| 布局快照 | WPF 快照环境 | 渲染规定尺寸与状态 | 无关键裁切/重叠；成员只在宽屏打开 | `artifacts/rc25/ui-snapshots/after-*`；最终矩阵写入 stage-20 任务记录 |
| 最终代码门 | 工作树待提交 | 运行 Fast、Full、Release 和差异检查 | 全部为 0 警告/0 错误且回归通过 | `pwsh ./scripts/verify.ps1 -Mode Fast`、`pwsh ./scripts/verify.ps1 -Mode Full`、Release 构建、`git diff --check` |
| 发布包 | 干净、已提交的精确 HEAD | 两次运行 `publish-client.ps1` 到独立目录并运行 verifier | 两份 `1.0.0-rc.25` ZIP 字节一致，manifest、x64、self-contained、秘密排除均通过 | `scripts/verify-client-release.ps1` 输出及 stage-20 任务记录中的 commit/SDK/长度/SHA-256 |

未实际执行的人工 DPI/托盘/更新交接或发布检查必须标注为 `未验证`；WPF `RenderTargetBitmap` 快照不能替代这些人工窗口验收。
