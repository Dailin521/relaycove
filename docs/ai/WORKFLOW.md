# RichChat 简化开发工作流

## 1. 开始前

1. 依次读 `docs/ai/README.md`、根计划、STATUS、WORKFLOW 和唯一活动计划。
2. 执行 `git status --short --branch`，确认在 `main`，保留无关改动和所有 `.verify/`。
3. 一次只处理用户明确提出的一个问题；不要顺带重构或追加第二项功能。

仓库代码和本轮实际命令优先于文档。协议问题以 Zulip 12.1 OpenAPI/官方文档为准。

## 2. 实现

优先最小、直接、可读的改动。按实际需要触及以下层，不为小问题强行走完整纵切片：

```text
Core -> Zulip.Client/Data -> ClientSession -> App
```

必须保持：

- App 不直接处理 Zulip DTO 或 SQLiteConnection。
- Core 不引用 MAUI、HTTP、JSON 或 SQLite。
- Zulip 是权威状态；缓存和 UI 不推测权限、成员或写入成功。
- 非幂等写入不自动重试，账号/会话切换后的晚到结果不回填。
- 密钥、正文和原始服务器错误不进入日志、异常、测试快照或包。

缺陷修复增加最窄的确定性回归测试；纯 XAML 微调可用结构测试或最小 App build。

## 3. 验证

逻辑、协议、缓存问题由 Agent 运行最窄相关测试。形成一个完整交付批次时再运行：

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

正式发布候选才运行：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

`Full` 不重复 Fast，也不运行历史 Web 检查或 Live。`Live` 必须有当轮明确的隔离凭据、目标和真实写授权，缺少任一项立即停止。

UI、布局、焦点、鼠标、键盘、字号、DPI 和真实窗口体验由用户通过 Visual Studio 验证。除非用户明确要求，Agent 不启动、移动、操作或截图用户窗口。

## 4. 文档

- 普通已确认问题只在 STATUS 留一条当前结论；不再为每个 UI 修正永久保留 Stage 日志。
- 只有复杂协议、数据迁移或正式 Release 才按需创建临时任务记录或 Release Note。
- 临时任务完成后将仍有价值的约束合并到总计划、STATUS 或交互规格，然后删除临时日志。
- STATUS 只保留当前版本、最近有效验证和仍未验证的事实，不累计历史流水账。

## 5. 提交与推送

用户确认后、且明确要求提交或推送时，执行最小 Git 事务：

1. 检查分支、上游和工作树。
2. 只暂存本问题的代码、测试和必要文档。
3. 检查 staged diff，提交并非强制推送到 `main`。
4. 确认远端 HEAD 和最终工作树；`.verify/` 始终排除。

提交请求本身不隐式授权 Full、Live、发布、tag、部署、真实 Realm 写入或其他外部副作用。
