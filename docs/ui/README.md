# RelayCove UI 文档索引

这里保存 RelayCove 双前端共享的视觉基线、交互规格、功能矩阵和验收规则。`RelayCove.Web` 是正式产品，`RelayCove.App` 在 Web 交互版本冻结后用原生 MAUI 复刻。

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
- 当前任务：[Stage 24.6 Windows Composer 持续光标](../ai/tasks/2026-08-19-stage-24-6-composer-caret.md)；此前 Stage 22/23/24 的 dated task 只作历史证据。真实 Realm 原生写与完整 Windows 人工验收仍开放。

## 重要边界

- 现有 Zulip 官方 Web 保留不修改；RelayCove.Web 与 MAUI 都直接连接同一 Realm，不新增服务端、BFF 或代理。
- 两端共享 Token、规格、功能矩阵与验收场景，不共享 UI 运行时代码。
- MAUI 必须使用原生 XAML、控件和 ViewModel，不得把 RelayCove.Web 或冻结 HTML 放进 WebView。
- Zulip 仍是用户、权限、频道、话题和消息的唯一事实源。
- Stage 22W 已实施任意附件选择/拖放/安全文件卡片、真实头像、可折叠频道/私信组、当前用户频道退订、完整引用/表情/reaction/edit/delete/star。MAUI 已原生实现受控头像/图片/文件附件、完整消息菜单、reaction/edit/delete/star、服务器搜索/saved、已知用户新会话和普通用户频道自助能力；这些原生真实 Zulip 写入仍需按门禁验收。完整成员关系、`@` 候选、presence 和管理员频道管理继续保持隐藏或明确不可用。
- 截图用于视觉比较；交互规格用于行为判断；两者都不能替代 Windows 真实窗口验收。
