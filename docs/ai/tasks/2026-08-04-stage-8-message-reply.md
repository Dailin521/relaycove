# 阶段 8 消息回复发送与引用展示

## 任务定义

- **任务名称：** 阶段 8 当前会话 Reply 选择、durable 发送、引用展示与导航
- **状态：** `已完成`
- **基准提交：** `a919992d6cdd7c646adf4b933b119e089dc79fce`
- **工作分支：** `agent/stage-8-message-reply`
- **相关方案章节：** 4.2、9.2–9.4、10.4、12.1–12.3、21.2、阶段 8；`DEC-010`、`DEC-017`、`DEC-034`、`DEC-035`

### 目标

在当前 Ready 会话中允许用户选择一条已确认消息作为回复目标，在输入区明确显示并可取消引用；发送时把同一正 `ReplyToMessageId` durable-first 落盘并沿用既有幂等 HTTP、失败重试和响应/回声合并。消息行显示已加载目标的发送者与正文；目标不在当前窗口时如实提示并允许用既有 Around 导航定位，不泄漏旧会话或旧账户引用。

### 已知事实

- `已验证`：绿色集成头 `a919992` 已包含 751/751 的 WindowActivated/Periodic Sync 切片；当前分支建立时工作树干净。
- `已验证`：Shared `SendMessageRequest`/`MessageDto`、Server 同会话回复校验与幂等载荷比较、Client SQLite pending/confirmed 表、统一 merge 和重试读取均已完整保存 `ReplyToMessageId`；无 Shared/Server 协议或 schema 缺口。
- `已验证`：新 Text pending 原先固定 `ReplyToMessageId=null`，且 Client HTTP transport 虽已序列化并严格比较响应 Reply 字段，入口校验仍拒绝任何非空 Reply；本任务把发送入口、transport 与 malformed incoming 校验一起收敛为 nullable 正 ID，并继续从 durable pending 复用原目标。
- `已验证`：当前消息选择状态保存已加载的 `MessageDto` 字典，presentation/WPF 只显示正文和发送状态，尚未暴露 Reply 操作、输入区引用上下文或消息行引用。
- `已验证`：现有 `SelectConversation(conversationId, targetMessageId)` 已用 Around 定位目标消息，可复用为未加载引用的显式导航，不需要新增消息端点。
- `已验证`：`@用户` 不能安全并入本任务：当前没有普通用户目录 API，Public 会话成员列表按既有契约返回 `ConversationTypeConflict`；若实现可用 @ 选择器需要另开服务端协议任务。
- `已验证`：Claude #63 前置 challenge 与 #64 当前差异 review 均因本机认证源优先级冲突失败，无 job、模型、费用或结论；Codex 继续负责边界、实现和验证，失败不冒充审查通过。

### 假设

- `假设`：只有当前 Ready 选择中仍存在的正服务端消息可成为新回复目标；pending 行不能被回复，避免把本地身份冒充服务端 ID。
- `假设`：引用摘要只使用当前选择已经加载且通过权威门的消息。目标缺失时显示“原消息未加载”，点击引用以既有 Around 导航到目标；不为每行自动制造额外网络请求。
- `假设`：新发送只有在 `PendingCommitted=true` 且输入和回复目标都仍是提交时值时，才清空输入与引用；发送期间用户切换的新引用不会被迟到结果清除。

### 范围

- 必须实现：
  - Send coordinator/runtime/shell 接受 nullable 正 ReplyToMessageId；shell 在当前选择锁内验证目标属于当前已加载确认消息，再创建 pending；retry 无条件复用数据库原值。
  - presentation 为确认与 pending reply 解析已加载目标的脱敏发送者/正文摘要；缺失目标明确标记但不伪造内容，引用可导航到真实目标。
  - WPF 每条确认消息提供 Reply 操作；composer 显示目标摘要和取消按钮。会话切换、账户切换、非 Ready、撤权和退出清除旧引用。
  - 覆盖目标校验、pending 早于 HTTP、201/200/Realtime/Sync 合并、失败重试原 ID、缺失目标、导航、发送期间改目标、旧 revision/旧账户隔离与日志脱敏。
- 允许修改：
  - `src/RelayCove.Client/Accounts/`、`Sync/`、`Storage/` 的输入校验、`MainWindow.xaml(.cs)` 及对应 Client 测试。
  - 本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - `@用户`、普通用户目录/成员缓存、Shared/Server 协议、SQLite schema/migration、附件、链接识别、复制、日期/新消息分割线或新依赖。
  - 自动加载每条缺失回复目标、编辑/撤回、搜索、VPS/双客户端实机 Gate。

### 验收标准

- [x] 只有当前 Ready 会话中的 confirmed 正 ID 行可进入 Reply；pending/旧会话/旧账户/缺失行被拒绝。composer 可见显示发送者与正文摘要并可取消。
- [x] 新回复 pending 在 HTTP 前原子保存精确 ReplyToMessageId；201/200、response/realtime/sync 竞争只提升同一行，失败重试复用原目标，Sent 不回退。
- [x] 已加载回复目标在消息行显示被回复发送者与正文；未加载目标明确提示且点击后用 Around 定位，非 Ready/撤权不显示缓存引用。
- [x] PendingCommitted 后只清除仍未改变的输入与 reply context；发送期间新选择、会话/账户切换和迟到结果不覆盖当前 UI。
- [x] Fast/Full、send/presenter/shell/WPF 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；真实服务器/VPS/第二客户端如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~MessageReply|FullyQualifiedName~MessageSend|FullyQualifiedName~MessageListPresenter|FullyQualifiedName~ClientAccountShell"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、ReplyToMessageId 幂等语义、账户/会话权威门或引入新依赖。
- 需要用本地 pending ID 伪造服务端回复目标、自动泄漏旧 scope 引用，或读取 VPS 配置才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 4.2、9.2–9.4、10.4、12.1–12.3、21.2、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现 Reply 选择、durable 发送、引用展示与 Around 导航；不实现 @、用户目录、附件或其他消息视觉功能。
先冻结当前选择/confirmed ID 门和 PendingCommitted UI 清理条件，再沿用既有 pending/HTTP/merge/retry 路径。
所有旧会话、旧账户、非 Ready 与撤权路径必须 fail-closed；正文、身份和回复 ID不得进入日志或 ToString。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- `ClientMessageSendCoordinator`、runtime 与账户 shell 现在接受 nullable 正 Reply ID；shell 只在当前 Ready selection 锁内接受已加载确认消息，HTTP transport 不再误拒合法 Reply，incoming/pending 继续拒绝非正值。
- durable pending 在 POST 前保存精确目标；201/200、Realtime 与 Sync promotion、失败显式重试继续复用同一请求键和 Reply 载荷，Sent 不被迟到失败降级。
- 消息 presentation 展示已加载引用发送者/正文，缺失目标如实提示并复用 Around；确认行提供“回复”，composer 提供引用摘要/取消，pending 只展示引用而不能成为新目标。
- composer 使用会话、Ready 与 Reply 操作的单调上下文版本抵御 ABA；迟到发送结果只有在内容、会话、目标与版本均未改变且 pending 已提交时才清空。
- `DEC-037` 冻结上述边界；`@用户` 因无普通用户目录继续拆分后续任务，无 Shared/Server/schema/migration/依赖变化。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | Fast / Full | Fast 及固定代码提交 `239d1ce` 上的两次 Full 均通过；Release 0 警告、0 错误，Shared 35 + Server 175 + Client 549 + Updater 1 = 760/760。 |
| `已验证` | Reply 可靠性定向 | send/presenter/shell/cache/sync 关键集每轮 113 项；Release 连续 10 轮 1,130/1,130，覆盖合法/非法目标、durable-before-HTTP、201/200、Realtime/Sync promotion、原键重试、缺失目标 Around、选择切换与脱敏。 |
| `已验证` | EF / NuGet / 空白 | EF Core 报告无 pending model changes；8 个 source/test 项目均无已知 vulnerable package；`git diff --check` 通过。 |
| `已验证` | 真实 Windows WPF smoke | Release 主进程 PID 31528 取得非零句柄 46859264 且 `Responding=True`；第二实例 PID 36216 在 10 秒内退出码 0，同路径进程数保持 1；只清理本次精确 PID 后残留 0。 |
| `已验证` | Claude / Codex 复核 | Claude #63/#64 均因认证源优先级失败，无结论或费用；Codex 固定差异复核发现并修正 transport 误拒合法 Reply、incoming 非正 Reply 与 composer ABA 清理缺口，随后以上门禁通过。 |
| `未验证` | 真实 VPS / 双客户端 / Narrator | 按 M5 Gate 保留；本任务未读取 VPS 配置，也不以本机 smoke 冒充真实服务端 Reply 端到端。 |

### 文件范围

- 新增：无。
- 修改：Client Accounts send/runtime/shell/presentation、MainWindow XAML/code-behind、Storage incoming 校验、Sync send coordinator/transport；对应 shell/presenter/cache/sync 测试；`DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md` 与本任务记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-037`；当前 Ready 确认 ID 门、durable 原目标、缺失目标 Around 和版本化 composer context 是本切片冻结边界。
- 已知限制：`@用户` 需普通用户目录/公共频道解析协议，明确不在本任务；真实 VPS/双客户端留到 M5 Gate。

### 下一步

- 仅快进集成 `239d1ce` 及本完成记录，然后继续阶段 8 下一独立切片；`@用户` 必须先冻结普通用户目录协议。
