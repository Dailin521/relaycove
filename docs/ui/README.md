# RelayCove UI 文档索引

这里保存 RelayCove 的视觉基线、交互规格和从 Web 原型迁移到原生 MAUI 的工程规则。

## 权威顺序

先区分“当前实现事实”和“目标要求”：

- 判断已经实现什么时，仓库当前代码、测试和实际运行证据优先于所有文档与原型；原型中能点击不代表 MAUI 已实现。
- 判断下一步应实现什么时，根目录的 [重建开发计划](../../RelayCove_Zulip_MAUI_重建开发计划.md) 决定产品范围、架构、安全和 Zulip 协议边界；[交互规格](INTERACTION_SPEC.md) 决定已批准行为；[UI 开发工作流](DEVELOPMENT_WORKFLOW.md) 决定交付顺序；[冻结 Web UI 基线](baselines/chat-ui-v1/README.md) 只提供视觉和可操作参考。

代码与目标规格发生偏差时，应明确记录“当前行为”和“目标行为”，修复后用当前测试/运行证据确认，不能用文档声明覆盖仓库事实。

## 当前基线

- 基线 ID：`chat-ui-v1`
- 状态：Frozen
- 冻结日期：2026-08-12
- 目标视口：1440×900；补充检查 1024×768
- 原型入口：[RelayCove-UI-Playground.html](baselines/chat-ui-v1/RelayCove-UI-Playground.html)
- 下一阶段计划：[Stage 22 — Native Chat UI](../ai/tasks/2026-08-12-stage-22-native-chat-ui.md)

## 重要边界

- MAUI 必须使用原生 XAML、控件和 ViewModel，不得把冻结 HTML 放进 WebView。
- Zulip 仍是用户、权限、频道、话题和消息的唯一事实源。
- 频道管理、附件、反应、搜索、`@` 成员等超出 Stage 21 的能力只能按独立能力门实施。
- 截图用于视觉比较；交互规格用于行为判断；两者都不能替代 Windows 真实窗口验收。
