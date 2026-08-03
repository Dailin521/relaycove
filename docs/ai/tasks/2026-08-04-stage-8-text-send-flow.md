# 阶段 8 Text 输入、pending、幂等发送与失败重试

## 任务定义

- **任务名称：** 阶段 8 Text 消息输入、持久 pending、HTTP 幂等发送与失败重试
- **状态：** `进行中`
- **基准提交：** `c237bc04393361b0ab7d80376f121d4e5621561e`
- **工作分支：** `agent/stage-8-text-send-flow`
- **相关方案章节：** 4.2、9.2–9.4、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-010`、`DEC-014`、`DEC-017`、`DEC-026`、`DEC-027`、`DEC-034`

### 目标

在当前已授权会话中接入可日常使用的 Text 发送闭环：输入先按服务端同口径验证并持久化本账户 pending，再以同一 `ClientMessageId` 调用 HTTP；201/200 响应、Realtime 回声和后续 Sync 最终只提升同一行。网络或不确定失败保留可见失败行，用户显式重试复用原键和原载荷；撤权、认证失效、账户切换和进程恢复必须 fail-closed。

### 已知事实

- `已验证`：基准 `c237bc0` 已仅快进并推送到 `agent/v1-integration`；当前分支建立时工作树干净，最终 Fast/Full 通过 Shared 35、Server 175、Client 493、Updater 1，共 704/704，Debug/Release 构建 0 警告、0 错误。
- `已验证`：Shared/Server 已冻结 `SendMessageRequest` 与 `POST /api/messages`：Text 精确保留，1–4000 个有效 Unicode scalar、非全空白，只允许 TAB/CR/LF 控制字符；新建 201、相同重放 200、载荷冲突 409，只有新插入在提交后尝试一次 `NewMessage`。
- `已验证`：客户端 schema 已支持 `ServerMessageId=NULL` 的本账户 pending、`Sending/Failed/Sent` 状态以及 `(SenderId, ClientMessageId)` 唯一键；统一 merge 已能把相容响应/回声提升为同一行并以服务端时间覆盖 pending 时间，迟到确认不会增加未读或通知。
- `已验证`：当前 `AddPendingMessageAsync` 只插入 Sending 行，尚不发布状态、标记失败或准备重试；有界消息页面明确排除 pending，当前 UI/presenter 也只有正服务端消息 ID，因此发送状态尚不可见。
- `已验证`：现有 History/read-through transport 已实现动态 Bearer、一次 401 refresh、稳定撤权错误分类与脱敏日志，可作为发送 transport 的边界参考；runtime 已有可终止 operation ownership，coordinator 已有当前 selection/revision/Dispatcher 门。

### 假设

- `假设`：本切片只发送 `MessageType.Text`，`ReplyToMessageId=null`、AttachmentIds/MentionUserIds 为空；回复、@、附件和富文本另开切片，不用占位字段冒充完成。
- `假设`：最新视图最多读取 50 条已确认消息，并另行有界显示至多 50 条本账户 outstanding pending/failed 行；pending 使用 `LocalId + ClientMessageId` 身份，绝不伪造服务端消息 ID。
- `假设`：POST 发生网络/timeout/429/5xx 或响应不确定时不自动重发写请求；先条件性标记 Failed，用户显式重试原子执行 `Failed -> Sending` 并复用完全相同的 `ClientMessageId` 和载荷，服务端 200 replay 负责收敛已提交但响应丢失的情况。
- `假设`：Enter 发送，Ctrl+Enter 插入换行；输入只有在 pending 已持久提交后才清空。切换会话不取消已提交发送，注销/账户终止会收敛 flight；崩溃遗留 Sending 在下次 scope 初始化时恢复为 Failed。

### 范围

- 必须实现：
  - cache 对 pending 的原子插入、失败标记、显式重试准备、启动恢复和有界读取；所有状态转换条件化，Sent 不回退，撤权/未知/fatal 不暴露载荷。
  - Text 客户端验证、发送 HTTP transport/coordinator、一次 401 refresh、严格成功响应/不可变载荷校验、稳定错误分类，以及 SendResponse 统一 merge；稳定撤权进入既有 purge，认证失效结束账户会话。
  - runtime/shell 对发送 flight 的账户所有权、同一 pending 重入合流、旧账户/旧选择迟到隔离；pending 提交后即触发当前消息视图刷新，响应/回声竞争最终一行。
  - WPF 多行输入、Enter/Ctrl+Enter、发送按钮、发送中/失败状态和失败行重试；UI 只在 Dispatcher 更新，消息项不伪造 ServerMessageId，输入/正文不进入日志或 `ToString()`。
- 允许修改：
  - `src/RelayCove.Client/Storage/`、`Sync/`、`Accounts/`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
  - 对应 `tests/RelayCove.Client.Tests/`，以及本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - Reply、@、附件、图片/文件、链接/复制、日期/新消息分割线、编辑/撤回、搜索、草稿跨会话持久化和周期 Sync。
  - Shared/Server 协议、SQLite schema/migration、DPAPI/通知激活编码、新依赖或自动后台重发队列。

### 验收标准

- [ ] Text 验证与服务端当前口径一致，保留合法多行和首尾空白；非法 Unicode、空白、超长或不支持控制字符在落盘/HTTP 前拒绝，正文不进入诊断。
- [ ] pending 先于 HTTP 原子持久化并立即可见；最新视图总量有界且 pending 不伪造服务端 ID。201/200、Realtime-first、Response-first、Sync-late 与重复回声只产生一行 Sent 消息。
- [ ] 瞬态/不确定失败条件性 `Sending -> Failed`；显式重试原子 `Failed -> Sending` 并复用原键/载荷，同一行重复点击合流。Sent 不回退，迟到失败不覆盖确认，崩溃遗留 Sending 可恢复重试。
- [ ] 401 最多刷新一次；稳定撤权 purge 并隐藏行，认证失效结束账户；400/403/409/429/5xx、非法 JSON/载荷和取消分类明确，不自动重放不确定 POST。
- [ ] Enter 发送、Ctrl+Enter 换行，pending 提交后才清输入；无当前 Ready 会话、非法输入或进行中的同一重试不能发送。会话/账户切换和注销的迟到结果不覆盖当前 UI 或跨 scope 发布。
- [ ] Fast/Full、pending/send/response-echo/coordinator/WPF 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；无真实账户/VPS/第二客户端的场景如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~MessageSend|FullyQualifiedName~PendingMessage|FullyQualifiedName~ClientAccountShell"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、账户隔离/权威快照/撤权语义、通知激活编码或引入新依赖。
- 需要自动重发不确定 POST、伪造服务端 ID、把未持久化输入显示为已发送，或允许旧账户发送结果进入当前 scope 才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 4.2、9.2–9.4、10.4、12.1–12.3、12.6–12.8、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现 Text 输入、持久 pending、HTTP 幂等发送、状态显示与失败重试，不实现回复、@、附件、搜索或自动后台重发。
先冻结本地状态转换、响应/回声竞争、runtime 所有权和 Dispatcher 边界，再写最小 cache、transport、coordinator 与 UI 路径。
所有旧账户/旧选择迟到结果、未完成权威快照和撤权路径必须 fail-closed；正文、身份和幂等键不得进入日志。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- `已验证`：基准最终 Fast/Full 通过，Debug/Release 构建 0 警告、0 错误，704/704 测试通过。
- `未验证`：实现后门禁、真实 Windows UI 与真实账户场景尚未执行。

### 文件范围

- 待完成。

### 决策与限制

- 待完成。

### 下一步

- 完成本切片后继续阶段 8 的回复、@、可用性细节或周期同步切片，以届时工程方案缺口为准。
