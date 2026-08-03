# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 仓库现状

RelayCove 已完成**可构建工程骨架与认证存储基线**：`RelayCove.sln` 包含 Client、Server、Shared、Updater 四个源项目和四个镜像 xUnit 测试项目；Server 已有认证共享契约、Users/RefreshTokens SQLite 迁移、密码与 token 哈希服务，但尚未实现可调用的登录、消息、同步、通知等业务端点。

统一验证脚本会真实执行还原、构建和测试；只有实际运行成功的模式才能标成通过。当前阶段、绿色提交和下一任务以 `docs/ai/V1_EXECUTION.md`、`docs/ai/STATUS.md` 与活动任务为准，不要依赖历史聊天。

## 常用命令

### Claude Second Brain MCP

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

### .NET 验证

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast    # 开发循环：还原、Debug 构建、测试
pwsh ./scripts/verify.ps1 -Mode Full    # 提交与阶段验收：格式检查、Release 构建、全部测试、git diff --check
```

`Fast` 用于开发循环；`Full` 是提交和任务完成前的基线。脚本任一底层命令非零都会失败；不得用单独的窄检查替代任务要求的模式。

## 架构要点

规格文档 `RelayCove_工程落地方案.md` 是产品与架构的唯一真源（约 2600 行，25 节）。按需读取相关章节，不要整份加载。

### 四个项目

| 项目 | 职责 | 边界 |
| --- | --- | --- |
| `RelayCove.Shared` | DTO、枚举、错误码、协议版本 | 不放服务端实体、ViewModel、UI、业务逻辑 |
| `RelayCove.Server` | ASP.NET Core API、SignalR Hub、EF Core、SQLite | — |
| `RelayCove.Client` | WPF UI、本地 SQLite 缓存、同步、通知、托盘 | — |
| `RelayCove.Updater` | 等待主程序退出、启动安装包 | 第一版不做回滚 |

测试镜像源项目：`tests/<Project>.Tests/`。

### 可靠性契约摘要

这是整个项目的立项理由，任何消息相关实现都必须保持：

**先入库、后推送。** `POST /api/messages` 在权限事务内使用 INSERT-first 和 `UNIQUE(SenderId, ClientMessageId)`：新建提交后返回 `201` 并只尝试一次 `NewMessage`，相同载荷重放返回 `200` 且不再推送，相同键不同载荷返回 `409 IdempotencyKeyReuse`。推送失败由周期同步补偿；SignalR 不是授权边界或可靠消息源。

**单一合并路径。** Realtime、Sync、History、SendResponse 共用一个本地事务内合并函数。`LocalMessages` 用 `LocalId` 作为本地主键、可空唯一 `ServerMessageId` 保存服务端身份；合并必须返回 `Inserted`、`PendingPromoted`、`Duplicate` 或 `Conflict`，重复到达不能重复增加未读、更新预览或创建通知候选。

```text
ProcessIncomingMessage(MessageDto message, IncomingMessageSource source)
// source: Realtime | Sync | History | SendResponse
```

**固定上界逐页同步。** 客户端以按 `AccountScopeId` 隔离的 `LastSyncCursor` 调用 `GET /api/sync?cursor=...&snapshotUpperBound=...`。服务端首页捕获 `SnapshotUpperBound`，响应 `Messages / NextCursor / SnapshotUpperBound / HasMore`；客户端验证不变量，并在一个本地事务内合并整页、更新未读和推进游标。权限空洞或空可见页也必须前进到上界；`409 SyncCursorInvalid` 不能静默归零。

**通知与撤权 fail-closed。** `IsNotificationHandled` 是唯一逐消息通知真源，只有串行 `NotificationCoordinator` 能调用 Toast；同步轮次用原子 gate 处理 Round/Recovery 候选。私有频道当前成员可懒加载全部历史，`LastReadMessageId` 只管已读；撤权后先建立 deny-set 与持久 tombstone，再清理缓存和 Toast，迟到 Realtime/History 不能复活会话。

### 服务端安全基线

权限校验必须在服务端：管理员接口校验 `IsAdmin == true`，搜索按 `ConversationMembers` 过滤，附件下载校验所属会话权限，都不能只靠客户端隐藏入口。附件物理文件名用 `{AttachmentId}_{RandomSuffix}{Extension}`，绝不使用原始文件名，防目录穿越。日志不得写明文密码、完整 Token、密钥、附件内容。

API 失败统一使用 `ApiErrorResponse` 的稳定字符串 `Code`；客户端不得解析 `Message` 分支。未知用户、错误密码和禁用账号统一为 `AuthenticationFailed`，避免账号枚举。Login request/response 的 `ToString()` 已脱敏，但任何日志代码仍不得显式记录密码、Access Token 或 Refresh Token。

认证存储遵循 `DEC-005`：登录名为 3–64 个 ASCII 字符并用 invariant-uppercase `NormalizedUserName` 唯一查找，Unicode 姓名只放 `DisplayName`。SQLite GUID 为小写 `D` 文本，时间为固定 UTC 文本；refresh token 只存 43 字符 SHA-256 Base64Url 哈希，密码使用配置化 IdentityV3 `PasswordHasher`。迁移只通过显式运维动作应用，服务启动不得隐式改库；当前原生 SQLite 显式固定到无已知漏洞的 `3.53.3`。

### 客户端约束

消息列表用虚拟化；集合更新回到 UI 线程；上传下载和缩略图不阻塞 UI；关闭窗口默认隐藏到托盘并保留 SignalR 连接与通知能力，真正退出只走托盘菜单。单实例激活必须通过实现探针在 `AppInstance` 与 `Mutex + Named Pipe` IPC 中选型，并把完整 `MessageTarget` / `UnreadOverviewTarget` 转交已有实例；不能只激活窗口。

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
