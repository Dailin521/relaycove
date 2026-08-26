# RelayCove AI 文档索引

Updated: 2026-08-26

## 当前阅读顺序

1. 根目录 `RelayCove_Zulip_MAUI_重建开发计划.md`：产品、架构、安全与验收边界。
2. `docs/ai/STATUS.md`：当前提交、当前验证证据和仍未关闭的门禁。
3. `docs/ai/WORKFLOW.md`：开发、测试、复核和外部副作用规则。
4. 当前活动问题是 `tasks/2026-08-25-stage-38-official-presence-display.md`，父计划是 `tasks/2026-08-25-v2-optimization-plan.md`；已完成的 `v2.2.0` 发布记录是 `tasks/2026-08-26-v2-2-release.md`。Stage 38 和 Stage 32/34 的未完成人工确认不因发布而关闭。
5. UI 工作再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 和 `DEVELOPMENT_WORKFLOW.md`。

仓库代码、测试和本轮实际命令证据优先于文档。文档中的历史测试数、分支、包哈希和截图只适用于其明确记录的提交，不能当成当前树证据。

## Task 状态

- **Active**：Stage 38 Zulip 官方在线状态与个人状态显示的最终 Visual Studio 视觉确认；父计划为 V2 总优化计划。
- **Pending confirmation**：Stage 34 reaction 表情面板定位与 Stage 32 收藏消息精确锚点仍等待用户最终确认。
- **Latest completed**：Stage 37 当前会话实时入站消息稳定追加及原生底部偏移动画已通过用户 Visual Studio 人工确认并随 `main@b0fb6ee` 交付；Stage 36 Windows 系统托盘提醒、未读预览与会话点击跳转已随 `main@f221424` 交付。
- **Latest release**：`2026-08-26-v2-2-release.md` 记录已完成的 `v2.2.0` Windows x64 正式 GitHub Release；V2.1 和首版记录继续作为历史证据。
- **Earlier completed**：Stage 26 引用显示修复已随 `main@a22b05b` 提交并推送；Stage 25 采用统一私聊/私有群聊模型，其经授权的 Realm 频道归档事实仍只以该任务记录为准。
- **Earlier completed**：Stage 24.10 已随 `main@3b6f814` 提交并推送；其工作日志仍保留当时实际记录的验证边界，不补写未经记录的人工结果。
- **Earlier completed**：Stage 24.8 与 Stage 24.9 已随 `main@faa5a5e` 提交并推送。
- **Earlier completed**：`2026-08-19-stage-24-7-channel-topic-tree.md` 已随 `main@a750dcd` 交付。
- **Historical**：Stage 22、Stage 23、Stage 24/24.1、Stage 24.2、Stage 24.3 和 Stage 24.4 的 dated task；相关实现已经进入 `main`，这些文件只保留决策与历史证据。
- **Open external gates**：Stage 21 的最终 MAUI 人工密码登录、安装包实装与干净 Windows 11 VM 验收仍未关闭，因此 Stage 21 记录不能废弃。

## 测试术语

- 五个 .NET 测试项目都是 Visual Studio Test Explorer 可发现的 xUnit 工程。
- `Fast`：MAUI/.NET Debug build 与四个普通测试工程。
- `Full`：MAUI/.NET Release build、四个普通测试工程、自包含 Windows publish、ZIP 与安全检查；不重复 Fast，也不运行历史 Web 检查。
- `Live`：只运行 `RelayCove.Zulip.LiveTests` 的显式真实 Realm 模式；不属于 Fast/Full，且必须有独立写授权。
