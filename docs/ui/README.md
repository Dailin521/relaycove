# RelayCove UI 文档索引

这里保存 RelayCove 双前端共享的视觉基线、交互规格、功能矩阵和验收规则。`RelayCove.Web` 是正式产品，`RelayCove.App` 在 Web 交互版本冻结后用原生 MAUI 复刻。

## 权威顺序

先区分“当前实现事实”和“目标要求”：

- 判断已经实现什么时，仓库当前代码、测试和实际运行证据优先于文档与基线；冻结 HTML 中能点击不代表正式 Web 数据能力或 MAUI 已实现。
- 判断下一步应实现什么时，根目录的 [重建开发计划](../../RelayCove_Zulip_MAUI_重建开发计划.md) 决定产品范围、架构、安全和 Zulip 协议边界；[交互规格](INTERACTION_SPEC.md) 决定两端共享行为；[UI 开发工作流](DEVELOPMENT_WORKFLOW.md) 决定 Web 先行、冻结版本、MAUI 原生对齐的顺序；[冻结基线](baselines/chat-ui-v1/README.md) 提供初始视觉和交互来源。

代码与目标规格发生偏差时，应明确记录“当前行为”和“目标行为”，修复后用当前测试/运行证据确认，不能用文档声明覆盖仓库事实。

## 当前基线

- 基线 ID：`chat-ui-v1`
- 状态：Frozen
- 冻结日期：2026-08-12
- 目标视口：1440×900；补充检查 1024×768
- 原型入口：[RelayCove-UI-Playground.html](baselines/chat-ui-v1/RelayCove-UI-Playground.html)
- 当前任务：[Stage 22W / 22M 双前端](../ai/tasks/2026-08-12-stage-22-native-chat-ui.md)；22W active，22M planned

## 重要边界

- 现有 Zulip 官方 Web 保留不修改；RelayCove.Web 与 MAUI 都直接连接同一 Realm，不新增服务端、BFF 或代理。
- 两端共享 Token、规格、功能矩阵与验收场景，不共享 UI 运行时代码。
- MAUI 必须使用原生 XAML、控件和 ViewModel，不得把 RelayCove.Web 或冻结 HTML 放进 WebView。
- Zulip 仍是用户、权限、频道、话题和消息的唯一事实源。
- 图片附件、真实头像与只读消息菜单已由 Stage 22W 独立能力门实施；频道管理、非图片附件、反应、搜索、`@` 成员等仍须按后续独立能力门实施。
- 截图用于视觉比较；交互规格用于行为判断；两者都不能替代 Windows 真实窗口验收。
