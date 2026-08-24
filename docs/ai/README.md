# RelayCove AI 文档索引

Updated: 2026-08-24

## 当前阅读顺序

1. 根目录 `RelayCove_Zulip_MAUI_重建开发计划.md`：产品、架构、安全与验收边界。
2. `docs/ai/STATUS.md`：当前提交、当前验证证据和仍未关闭的门禁。
3. `docs/ai/WORKFLOW.md`：开发、测试、复核和外部副作用规则。
4. 当前没有活动开发任务；首个 MAUI 版本周期由 `tasks/2026-08-24-v2-alpha1-release.md` 收口。此前带日期的 Stage 记录只作历史证据。
5. UI 工作再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 和 `DEVELOPMENT_WORKFLOW.md`。

仓库代码、测试和本轮实际命令证据优先于文档。文档中的历史测试数、分支、包哈希和截图只适用于其明确记录的提交，不能当成当前树证据。

## Task 状态

- **Active**：无。第一版 MAUI 开发周期已结束，后续需求从新的活动任务开始。
- **Latest completed**：Stage 29 完整 Unicode 表情目录已随 `main@a56e8e9` 交付；Stage 30 消息气泡与搜索尾部修正已随 `main@57c8145` 交付。
- **Release closure**：`2026-08-24-v2-alpha1-release.md` 记录 `v2.0.0-alpha.1` Windows x64 预发布包、校验值和仍未关闭的外部门禁。
- **Earlier completed**：Stage 26 引用显示修复已随 `main@a22b05b` 提交并推送；Stage 25 采用统一私聊/私有群聊模型，其经授权的 Realm 频道归档事实仍只以该任务记录为准。
- **Earlier completed**：Stage 24.10 已随 `main@3b6f814` 提交并推送；其工作日志仍保留当时实际记录的验证边界，不补写未经记录的人工结果。
- **Earlier completed**：Stage 24.8 与 Stage 24.9 已随 `main@faa5a5e` 提交并推送。
- **Earlier completed**：`2026-08-19-stage-24-7-channel-topic-tree.md` 已随 `main@a750dcd` 交付。
- **Historical**：Stage 22、Stage 23、Stage 24/24.1、Stage 24.2、Stage 24.3 和 Stage 24.4 的 dated task；相关实现已经进入 `main`，这些文件只保留决策与历史证据。
- **Open external gates**：Stage 21 的最终 MAUI 人工密码登录、安装包实装与干净 Windows 11 VM 验收仍未关闭，因此 Stage 21 记录不能废弃。

## 测试术语

- 五个 .NET 测试项目都是 Visual Studio Test Explorer 可发现的 xUnit 工程。
- `Fast`：格式检查、Debug build、四个普通测试工程，以及 Web typecheck/unit/production build。
- `Full`：在 Fast 基础上增加 Release、包与仅本地 fake-HTTP Playwright 等交付门禁。
- `Live`：只运行 `RelayCove.Zulip.LiveTests` 的显式真实 Realm 模式；不属于 Fast/Full，且必须有独立写授权。
