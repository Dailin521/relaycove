# RelayCove AI 文档索引

Updated: 2026-08-19

## 当前阅读顺序

1. 根目录 `RelayCove_Zulip_MAUI_重建开发计划.md`：产品、架构、安全与验收边界。
2. `docs/ai/STATUS.md`：当前提交、当前验证证据和仍未关闭的门禁。
3. `docs/ai/WORKFLOW.md`：开发、测试、复核和外部副作用规则。
4. 当前活动记录是 `tasks/2026-08-19-stage-24-9-official-topic-menu.md`；此前带日期的 Stage 记录只作历史证据。
5. UI 工作再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 和 `DEVELOPMENT_WORKFLOW.md`。

仓库代码、测试和本轮实际命令证据优先于文档。文档中的历史测试数、分支、包哈希和截图只适用于其明确记录的提交，不能当成当前树证据。

## Task 状态

- **Active**：`2026-08-19-stage-24-9-official-topic-menu.md`，官方式话题行与操作菜单已完成相关确定性验证，等待用户在 Visual Studio 中完成 UI/交互确认；在确认和新的明确提交/推送授权前保持未提交。
- **Local predecessor**：`2026-08-19-stage-24-8-channel-settings.md` 仍在同一未提交 `main` 工作树中，已完成确定性与离线副屏预览证据，但尚未收到 Visual Studio 人工确认或提交授权。
- **Latest completed**：`2026-08-19-stage-24-7-channel-topic-tree.md`，频道与话题树形导航已随 `main@a750dcd` 提交并推送；该工作日志没有补写未经明确记录的 Visual Studio 人工结果。
- **Historical**：Stage 22、Stage 23、Stage 24/24.1、Stage 24.2、Stage 24.3 和 Stage 24.4 的 dated task；相关实现已经进入 `main`，这些文件只保留决策与历史证据。
- **Open external gates**：Stage 21 的最终 MAUI 人工密码登录、安装包实装与干净 Windows 11 VM 验收仍未关闭，因此 Stage 21 记录不能废弃。

## 测试术语

- 五个 .NET 测试项目都是 Visual Studio Test Explorer 可发现的 xUnit 工程。
- `Fast`：格式检查、Debug build、四个普通测试工程，以及 Web typecheck/unit/production build。
- `Full`：在 Fast 基础上增加 Release、包与仅本地 fake-HTTP Playwright 等交付门禁。
- `Live`：只运行 `RelayCove.Zulip.LiveTests` 的显式真实 Realm 模式；不属于 Fast/Full，且必须有独立写授权。
