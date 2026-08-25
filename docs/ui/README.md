# RelayCove UI 文档索引

这里保存 RelayCove 历史双前端视觉基线、交互规格、功能矩阵和验收规则。自 Stage 25 的 2026-08-21 产品决策起，`RelayCove.App` 是唯一继续开发和交付的客户端；`RelayCove.Web` 与冻结基线只作历史证据，Web 源码是否从仓库移除另行决定。

## 权威顺序

先区分“当前实现事实”和“目标要求”：

- 判断已经实现什么时，仓库当前代码、测试和实际运行证据优先于文档与基线；冻结 HTML 中能点击不代表正式 Web 数据能力或 MAUI 已实现。
- 判断下一步应实现什么时，根目录的 [重建开发计划](../../RelayCove_Zulip_MAUI_重建开发计划.md) 决定产品范围、架构、安全和 Zulip 协议边界；[交互规格](INTERACTION_SPEC.md) 决定两端共享行为；[UI 开发工作流](DEVELOPMENT_WORKFLOW.md) 决定 Web 先行、冻结版本、MAUI 原生对齐的顺序；[MAUI 快速预览手册](MAUI_PREVIEW_WORKFLOW.md) 记录 Visual Studio、SDK、Hot Reload、外部编辑、副屏与 DPI 的本地操作；[冻结基线](baselines/chat-ui-v1/README.md) 只保留初始视觉历史与哈希证据，正式 Web 当前实现才是后续交互事实来源。

代码与目标规格发生偏差时，应明确记录“当前行为”和“目标行为”，修复后用当前测试/运行证据确认，不能用文档声明覆盖仓库事实。

## 当前基线

- 基线 ID：`chat-ui-v1`
- 状态：Frozen
- 冻结日期：2026-08-12
- 目标视口：1440×900；补充检查 1024×768
- 历史原型（只读）：[RelayCove-UI-Playground.html](baselines/chat-ui-v1/RelayCove-UI-Playground.html)；日常 Web 验收不再使用此入口
- 当前状态：[Stage 35 图片消息纯预览与右键下载](../ai/tasks/2026-08-25-stage-35-image-preview-context-download.md)已通过用户 Visual Studio 人工确认；[Stage 34 reaction 表情面板定位](../ai/tasks/2026-08-25-stage-34-reaction-picker-anchor.md)与[Stage 32 MAUI 收藏消息入口](../ai/tasks/2026-08-25-stage-32-saved-messages-entry.md)的精确锚点修正仍待最终确认。[Stage 33 reaction 按钮裁切](../ai/tasks/2026-08-25-stage-33-reaction-button-clipping.md)已确认修复。父计划为[第二版（V2）优化计划](../ai/tasks/2026-08-25-v2-optimization-plan.md)。

## 重要边界

- 现有 Zulip 官方 Web 保留不修改；RelayCove.Web 与 MAUI 都直接连接同一 Realm，不新增服务端、BFF 或代理。
- 两端共享 Token、协议安全边界与可复用验收场景，不共享 UI 运行时代码；Stage 25 的 MAUI 信息架构差异在交互规格第 1.1 节显式覆盖。
- MAUI 必须使用原生 XAML、控件和 ViewModel，不得把 RelayCove.Web 或冻结 HTML 放进 WebView。
- Zulip 仍是用户、权限、频道、话题和消息的唯一事实源。
- Stage 25 MAUI 左栏只显示一对一/self-DM 与合格私有空话题群聊；公开/旧多话题频道、group-DM、话题入口和公开频道浏览均无产品入口。旧活动频道已由用户授权全部归档；群成员与管理权限仍只使用权威读取，`@` 候选、presence 与复杂外部权限管理继续失败关闭。
- 截图用于视觉比较；交互规格用于行为判断；两者都不能替代 Windows 真实窗口验收。
