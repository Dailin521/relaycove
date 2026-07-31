# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 仓库现状

RelayCove 处于**设计先行**阶段，尚无业务代码：没有 `RelayCove.sln`、没有 `src/`、`tests/`、`scripts/`。仓库目前包含规格文档、AI 执行约定，以及唯一可运行的组件 `tools/claude-second-brain`（Node.js MCP）。

在解决方案脚手架建立之前，**不得声称项目可以构建或测试通过**，也不得创建“总是成功”的占位验证脚本。当前阶段、可构建状态和下一任务以 `docs/ai/STATUS.md` 为准，不要依赖历史聊天。

## 常用命令

### Claude Second Brain MCP（当前唯一可运行组件）

```powershell
cd tools/claude-second-brain
npm install
npm test                       # node --test test/*.test.mjs，不调用外部模型
npm run smoke                  # 经 MCP 完成一次真实 Claude 调用，会产生费用
npm start                      # 以 stdio 方式启动 MCP
```

跑单个测试用例：

```powershell
node --test --test-name-pattern "<用例名>" test/claude-runner.test.mjs
```

`smoke` 可用环境变量覆盖档位、预算和超时（默认 `opus` / `low` / `$0.10` / 60 秒）：
`CLAUDE_SECOND_BRAIN_SMOKE_MODEL`、`..._SMOKE_EFFORT`、`..._SMOKE_MAX_BUDGET_USD`、`..._SMOKE_TIMEOUT_SECONDS`。

### .NET 验证（脚手架完成后才可用）

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast    # 开发循环：还原、Debug 构建、测试
pwsh ./scripts/verify.ps1 -Mode Full    # 提交与阶段验收：格式检查、Release 构建、全部测试、git diff --check
```

`scripts/verify.ps1` 目前**不存在**，属于阶段 0 脚手架待办项。在它建立之前，验证只能依赖上面的 npm 命令和文档检查。

## 架构要点

规格文档 `RelayCove_工程落地方案.md` 是产品与架构的唯一真源（约 2600 行，25 节）。按需读取相关章节，不要整份加载。

### 计划中的四个项目

| 项目 | 职责 | 边界 |
| --- | --- | --- |
| `RelayCove.Shared` | DTO、枚举、错误码、协议版本 | 不放服务端实体、ViewModel、UI、业务逻辑 |
| `RelayCove.Server` | ASP.NET Core API、SignalR Hub、EF Core、SQLite | — |
| `RelayCove.Client` | WPF UI、本地 SQLite 缓存、同步、通知、托盘 | — |
| `RelayCove.Updater` | 等待主程序退出、启动安装包 | 第一版不做回滚 |

测试镜像源项目：`tests/<Project>.Tests/`。

### 两条不可动摇的可靠性约束

这是整个项目的立项理由，任何消息相关实现都必须保持：

**先入库、后推送。** 消息经 HTTP `POST /api/messages` 幂等写入 SQLite，事务提交后才由 SignalR 推送 `NewMessage`。SignalR 只是实时通道，不是可靠消息来源。服务端幂等键是 `UNIQUE(SenderId, ClientMessageId)`；客户端重试同一 `ClientMessageId` 必须返回已有 `MessageDto` 而非新建。

**单一入库路径。** 实时推送、断线补拉、历史加载、发送响应四种来源共用同一个客户端入库函数：

```text
ProcessIncomingMessage(MessageDto message, IncomingMessageSource source)
// source: Realtime | Sync | History | SendResponse
```

不要为补拉和推送写两套插入逻辑。客户端凭 `LocalAppState.LastSyncedMessageId` 在重连后调 `GET /api/sync?afterMessageId=xxx` 补拉，按 `MessageId` 升序处理；`LocalMessages` 已存在该 Id 就跳过插入且不重复通知。

去重分两层，不要混淆：`LocalMessages.IsRead` 管未读显示，`IsNotified` 管通知；通知去重另有 `LastNotifiedMessageId` 游标。

### 服务端安全基线

权限校验必须在服务端：管理员接口校验 `IsAdmin == true`，搜索按 `ConversationMembers` 过滤，附件下载校验所属会话权限，都不能只靠客户端隐藏入口。附件物理文件名用 `{AttachmentId}_{RandomSuffix}{Extension}`，绝不使用原始文件名，防目录穿越。日志不得写明文密码、完整 Token、密钥、附件内容。

### 客户端约束

消息列表用虚拟化；集合更新回到 UI 线程；上传下载和缩略图不阻塞 UI；关闭窗口默认隐藏到托盘并保留 SignalR 连接与通知能力，真正退出只走托盘菜单；单实例运行第一版用 Mutex + 激活窗口即可。

## 工作流

动手之前读：`AGENTS.md`、`RelayCove_工程落地方案.md` 的相关章节、`docs/ai/STATUS.md`、当前任务记录。执行、证据、评审与交接规则以 `docs/ai/WORKFLOW.md` 为准。

- 在 `agent/stage-<编号>-<slug>` 分支上工作；编辑前先看 `git status` 与基线。
- 一次只做一个可独立验证的纵向切片，不添加未被要求的基础设施、抽象或占位目录。
- 结论标注 `已验证` / `未验证` / `假设`；只有真正跑过检查才可以说它通过。事实优先级：仓库现状 > 官方文档 > 明确标注的假设。
- 遇到以下情况停下来问：无关的脏改动、基线本身失败、涉及密钥、破坏性操作、验收标准不清、兼容性变更、引入新的主要依赖。
- 验证通过后可以本地提交；推送和合并需要用户明确同意。
- 提交信息用简短命令式，如 `Add message deduplication`，不要把无关改动混在一起。PR 说明原因、影响、验证方式、限制和所处阶段；WPF 改动附截图。认证、迁移、同步、通知、更新、部署需要独立评审。

## 代码风格

- 四空格缩进，文件级命名空间，启用可空引用类型，一个文件一个公开类型。
- 类型和公开成员 `PascalCase`，局部变量 `camelCase`，接口以 `I` 开头，异步方法以 `Async` 结尾。
- I/O 一律异步并记录日志；绝不阻塞 WPF UI 线程。
- xUnit 测试命名 `Method_WhenCondition_ExpectedResult`；修 bug 必须补回归测试。
