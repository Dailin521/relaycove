# RelayCove UI 文档

RelayCove 当前只继续开发 Windows MAUI 客户端。代码和用户在 Visual Studio 中看到的真实窗口优先于历史截图或旧文字。

## 按需阅读

- `INTERACTION_SPEC.md`：当前交互约束；只读与本问题有关的章节。
- `MAUI_PREVIEW_WORKFLOW.md`：需要离线预览、副屏或 DPI 检查时阅读。
- `DEVELOPMENT_WORKFLOW.md`：复杂 UI 改动的补充流程；普通小修以 `docs/ai/WORKFLOW.md` 为准。
- `baselines/chat-ui-v1/`：早期 Web 视觉历史，仅在需要追溯时查看，不是当前实现或验收门禁。

## 当前边界

- MAUI 使用原生 XAML/ViewModel/Windows adapter，不使用 WebView。
- 左侧只显示一对一/self-DM 与受支持的私有空话题群聊。
- 公开频道、命名话题、多人私信和旧三栏导航没有产品入口。
- 一对一会话显示官方 presence 和个人状态；self-DM 与群聊不伪造聚合状态。
- Windows 通知、托盘悬浮和窗口焦点不能直接清未读，必须打开对应会话并满足最新位置/服务器确认条件。
- 视觉、布局、鼠标、键盘、焦点、字号和 DPI 最终由用户在 Visual Studio 验证。

Stage 38 已实现、由用户工程验证并随 `v2.2.0` 发布。当前没有单独活动的 UI Stage；后续按 `docs/ai/tasks/2026-08-25-v2-optimization-plan.md` 一次处理一个问题。
