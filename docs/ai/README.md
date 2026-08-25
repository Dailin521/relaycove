# RelayCove AI 文档索引

Updated: 2026-08-25

## 当前阅读顺序

1. 根目录 `RelayCove_Zulip_MAUI_重建开发计划.md`：产品、架构、安全与验收边界。
2. `docs/ai/STATUS.md`：当前提交、当前验证证据和仍未关闭的门禁。
3. `docs/ai/WORKFLOW.md`：开发、测试、复核和外部副作用规则。
4. 当前活动父计划是 `tasks/2026-08-25-v2-optimization-plan.md`；当前发布任务是 `tasks/2026-08-25-v2-1-release.md`。Stage 36 已通过用户 Visual Studio 人工确认并进入交付，Stage 32 精确锚点和 Stage 34 reaction 面板定位仍等待最终确认，此前其他 dated Stage 和发布记录只作历史证据。
5. UI 工作再读 `docs/ui/README.md`、`INTERACTION_SPEC.md` 和 `DEVELOPMENT_WORKFLOW.md`。

仓库代码、测试和本轮实际命令证据优先于文档。文档中的历史测试数、分支、包哈希和截图只适用于其明确记录的提交，不能当成当前树证据。

## Task 状态

- **Active**：V2 总计划与 `v2.1.0` 发布收口；当前没有未完成的独立代码问题。
- **Pending confirmation**：Stage 34 reaction 表情面板定位与 Stage 32 收藏消息精确锚点仍等待用户最终确认。
- **Latest completed**：Stage 36 Windows 系统托盘提醒、未读预览与会话点击跳转已通过用户 Visual Studio 人工确认并获交付授权；Stage 35 图片消息纯预览、Stage 34/33 reaction 修正与 Stage 32 收藏消息入口已随 `main@7df99da` 交付。
- **Release closure**：`2026-08-25-v2-1-release.md` 记录 `v2.1.0` Windows x64 正式 GitHub Release 的准备、校验和发布证据；`2026-08-24-v2-alpha1-release.md` 保留首版历史。
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
