# RelayCove Agent 指南

## 阅读顺序

编辑前依次阅读：

1. `docs/ai/README.md` —— 当前文档索引，以及活动任务与历史任务的边界。
2. `RelayCove_Zulip_MAUI_重建开发计划.md` —— 产品、架构、安全与验收的权威来源。
3. `docs/ai/STATUS.md` —— 已验证的当前状态与未关闭门禁。
4. `docs/ai/WORKFLOW.md` —— 执行与证据规则。
5. 仅阅读 `docs/ai/tasks/` 下明确标为活动的任务；带日期的历史记录只作证据，不能作为当前指令。

仓库证据优先于正式文档；官方 Zulip 12.1/OpenAPI 证据优先于假设。只有在当前工作树运行过指定命令后，才能将结果标为已验证。

## 架构边界

- `RelayCove.Web`：可独立部署的 TypeScript/React/Vite 客户端；仅包含浏览器 Zulip HTTP/会话适配器与 UI，不依赖 MAUI 运行时或 .NET UI。
- `RelayCove.App`：MAUI 视图/ViewModel、Windows 组合根与平台凭据/配置适配器。
- `RelayCove.Core`：领域模型、reducer、用例与公开接口；不得引用 MAUI、JSON、HTTP 或 SQLite。
- `RelayCove.Zulip.Client`：Zulip REST/事件协议与 DTO 映射；不得引用持久化或 UI。
- `RelayCove.Data`：SQLite 缓存与迁移；不得存放凭据或网络逻辑。
- `RelayCove.Zulip.LiveTests` 不属于普通构建/测试命令的一部分。

官方 Zulip Web 不作修改。`RelayCove.Web` 和 `RelayCove.App` 都直接连接同一 Zulip Realm；不得引入 RelayCove 服务端、代理、BFF、第二消息后端、过时 Zulip .NET SDK 或 WebView 渲染器。两个前端共享 Token、交互规格、能力矩阵与验收场景，但不共享 UI 运行时代码。

## 代码风格

使用四空格缩进、文件范围命名空间、可空引用类型、每文件一个公开类型、异步 I/O、取消令牌与确定性测试。公开类型/成员用 PascalCase，局部变量用 camelCase，接口以 `I` 开头，异步方法以 `Async` 结尾。xUnit 名称遵循 `Method_WhenCondition_ExpectedResult`。修复缺陷必须增加回归测试。

密钥不得出现在 `ToString`、异常、日志、快照、fixture 或包中。生产 HTTP 必须禁用重定向。非幂等消息发送禁止自动重试。

Web 用户可选择将 API Key 保存在浏览器 local storage，且“记住登录”是默认产品行为。注销必须清除持久和会话凭据；Key 不得进入 URL、日志、UI 文案或测试快照。

## 验证

开发期间运行：

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

交付提交前运行：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

`Live` 需要显式提供隔离测试凭据与写入确认；缺少配置必须失败关闭。不得使用个人账户或生产频道替代。

五个 .NET 测试项目均为 Visual Studio Test Explorer 可发现的 xUnit 项目。Fast/Full 运行四个普通项目 Core/Zulip.Client/Data/App；`RelayCove.Zulip.LiveTests` 仅在显式授权的 Live 模式运行。

工作前后检查 `git status`。保留无关的用户改动。未经显式授权，不得执行破坏性 Git 命令、强推、推送、打 tag、发布、修改 Zulip 主机、删除旧本地数据或执行外部写入。

认证、协议、同步、数据、outbox 与打包改动需要独立的只读复核。将未解决的 P0/P1 与未验证的 VM/Live 门禁记录在 STATUS，不得通过降低验收标准来规避。

## 提交与推送请求

实现所需的验证、独立复核和文档更新应在开发阶段完成，在请求或接受交付提交之前处理；不得把日常验证工作拖到用户说“提交”或“提交推送”之后。

当用户明确要求提交或推送时，将其视为最小 Git 事务：

1. 检查分支、上游和 `git status`。
2. 仅暂存已约定、在范围内的文件。
3. 检查暂存差异，创建提交，并以非强制方式推送到指定远端/分支。
4. 确认远端 HEAD 和最终工作区状态。

不得暗中向该事务加入 Fast/Full、广泛代码复核、文档重写、打包、启动应用、截图、Live 访问或部署。若确有尚未完成的强制提交前门禁，必须在开始提交前说明其预期耗时和副作用，并等待用户决定；不得把原本很短的提交请求意外变慢。

绝不暂存无关的用户改动。用户授权推送只授权该次推送；不代表授权强推、创建 tag、部署、运行 Live 测试或额外写入 Realm。

## MAUI 开发协作约定

除非用户明确要求隔离、并行工作或新分支，所有 MAUI 开发直接在 `main` 进行。一次只处理一个明确问题；完成代码修改后先交给用户人工确认，确认无误后即可按“提交与推送请求”规则提交并推送。不得在同一轮未经确认地叠加相邻功能、重构或第二个问题，以免来回修改。

用户统一通过 Visual Studio 打开 RelayCove 项目并进行 UI/交互快速验证。对于视觉、布局、焦点、鼠标、键盘、窗口和其他原生交互问题，Agent 只负责代码修改；除非用户明确要求，不启动、移动、操作、截图或复验用户的 MAUI 窗口。修改完成后应直接告知用户受影响的文件和最短验证动作。

不是视觉效果的问题（例如纯领域逻辑、状态投影、协议映射、缓存、回归缺陷）由 Agent 自行运行最小相关编译和确定性测试。不要将构建成功表述为 UI 人工验收，也不要为了常规 UI 修改自动执行 Full、真实 Realm 写入、Live、打包或复杂的端到端验收。

优先采用直接、最小、可读的实现；不为假设场景提前引入额外抽象层、复杂状态机、过度防御代码或大范围重构。这个效率约定不削弱既有不可谈判边界：不得泄露凭据，不得绕开服务端权威状态，不得自动重试非幂等写入，也不得在未授权时执行真实 Realm 写入或破坏性操作。
