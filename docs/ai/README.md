# RelayCove AI 文档索引

Updated: 2026-08-21

## 当前阅读顺序

1. 根目录 `RelayCove_Zulip_MAUI_重建开发计划.md`：产品、架构、安全与验收边界。
2. `docs/ai/STATUS.md`：当前提交、当前验证证据和仍未关闭的门禁。
3. `docs/ai/WORKFLOW.md`：开发、测试、复核和外部副作用规则。
4. 当前活动记录是 `tasks/2026-08-21-stage-25-unified-private-groups.md`；此前带日期的 Stage 记录只作历史证据。
5. UI 工作再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 和 `DEVELOPMENT_WORKFLOW.md`。

仓库代码、测试和本轮实际命令证据优先于文档。文档中的历史测试数、分支、包哈希和截图只适用于其明确记录的提交，不能当成当前树证据。

## Task 状态

- **Active**：`2026-08-21-stage-25-unified-private-groups.md`，MAUI 与共享 .NET 层的微信式统一会话及 `empty_topic_only` 私有频道群聊已形成待人工确认的本地候选；用户随后选择 MAUI 作为唯一后续客户端，并授权将 Realm 的 17 个活动频道全部归档。Web 源码本轮未改，是否从仓库移除另行决定；尚无提交或推送授权。
- **Previous local candidates**：Stage 24.11 至 Stage 24.17 仍保留在当前工作树；Stage 24.13 已取代 Stage 24.12 的详情呈现，Stage 24.17 已取代 Stage 24.15 的头像位置。Stage 24.14 与 Stage 24.17 已获用户人工确认，其余候选不因本轮开始而自动通过；七个候选均尚未提交或推送。
- **Latest completed**：Stage 24.10 已随 `main@3b6f814` 提交并推送；其工作日志仍保留当时实际记录的验证边界，不补写未经记录的人工结果。
- **Earlier completed**：Stage 24.8 与 Stage 24.9 已随 `main@faa5a5e` 提交并推送。
- **Earlier completed**：`2026-08-19-stage-24-7-channel-topic-tree.md` 已随 `main@a750dcd` 交付。
- **Historical**：Stage 22、Stage 23、Stage 24/24.1、Stage 24.2、Stage 24.3 和 Stage 24.4 的 dated task；相关实现已经进入 `main`，这些文件只保留决策与历史证据。
- **Open external gates**：Stage 21 的最终 MAUI 人工密码登录、安装包实装与干净 Windows 11 VM 验收仍未关闭，因此 Stage 21 记录不能废弃。

## 测试术语

- 五个 .NET 测试项目都是 Visual Studio Test Explorer 可发现的 xUnit 工程。
- `Fast`：格式检查、Debug build、四个普通测试工程，以及 Web typecheck/unit/production build。
- `Full`：在 Fast 基础上增加 Release、包与仅本地 fake-HTTP Playwright 等交付门禁。
- `Live`：只运行 `RelayCove.Zulip.LiveTests` 的显式真实 Realm 模式；不属于 Fast/Full，且必须有独立写授权。
