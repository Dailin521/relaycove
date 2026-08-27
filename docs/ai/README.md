# RichChat AI 文档索引

Updated: 2026-08-26

## 每次必读

1. `RelayCove_Zulip_MAUI_重建开发计划.md`：当前产品、架构和安全边界。
2. `docs/ai/STATUS.md`：当前版本、验证事实和未验证项。
3. `docs/ai/WORKFLOW.md`：本项目的最小开发与交付流程。
4. `docs/ai/tasks/2026-08-25-v2-optimization-plan.md`：唯一活动的长期优化计划。

UI 问题按需再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 或 `MAUI_PREVIEW_WORKFLOW.md`，不要默认加载历史冻结基线。

## 当前状态

- 当前产品：Windows MAUI-only 个人 MVP。
- 当前正式版本：`2.2.0`。
- 当前 `main` 基线：`a916791`。
- Stage 38 官方 presence 与个人状态已经实现、由用户在工程中验证并交付；不再是活动任务。
- 旧 Stage/Release 临时日志已删除，历史证据以 Git commit、tag、GitHub Release 和 `docs/releases/` 为准。
- 工作树中的 `.verify/` 是本机验证输出，必须保留且永不提交。

## 证据规则

仓库代码、测试和本轮实际命令优先于文档。旧提交的测试数、截图、哈希或发布结果只属于该提交，不能冒充当前树证据。Live、真实 Realm 写入、发布、tag、部署和推送都需要当轮明确授权。
