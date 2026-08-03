# 阶段 8 Text 输入、pending、幂等发送与失败重试

## 任务定义

- **任务名称：** 阶段 8 Text 消息输入、持久 pending、HTTP 幂等发送与失败重试
- **状态：** `已完成`
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

- [x] Text 验证与服务端当前口径一致，保留合法多行和首尾空白；非法 Unicode、空白、超长或不支持控制字符在落盘/HTTP 前拒绝，正文不进入诊断。
- [x] pending 先于 HTTP 原子持久化并立即可见；最新视图总量有界且 pending 不伪造服务端 ID。201/200、Realtime-first、Response-first、Sync-late 与重复回声只产生一行 Sent 消息。
- [x] 瞬态/不确定失败条件性 `Sending -> Failed`；显式重试原子 `Failed -> Sending` 并复用原键/载荷，同一行重复点击合流。Sent 不回退，迟到失败不覆盖确认，崩溃遗留 Sending 可恢复重试。
- [x] 401 最多刷新一次；稳定撤权 purge 并隐藏行，认证失效结束账户；400/403/409/429/5xx、非法 JSON/载荷和取消分类明确，不自动重放不确定 POST。
- [x] Enter 发送、Ctrl+Enter 换行，pending 提交后才清输入；无当前 Ready 会话、非法输入或进行中的同一重试不能发送。会话/账户切换和注销的迟到结果不覆盖当前 UI 或跨 scope 发布。
- [x] Fast/Full、pending/send/response-echo/coordinator/WPF 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；无真实账户/VPS/第二客户端的场景如实保留未验证。

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

- `4cad2b3769eb555f009f3f3eaf1e93b2c642a0c6` 增加与服务端逐 Unicode scalar 一致的 Text 验证、每会话最多 50 条的 durable pending/failed 读取和条件化 `Sending/Failed/Sent` 转换；只有进程首次打开该 scope 时把崩溃遗留 Sending 恢复为 Failed，同进程第二个 cache 不误降级活动发送。
- 新增无自动瞬态重试的 `POST /api/messages` transport/coordinator：动态 Bearer、一次 401 refresh、201/200 严格 DTO/当前发送者/不可变载荷校验、稳定撤权 purge、响应/Realtime 统一 merge，以及显式原键重试和同键 single-flight。
- runtime/shell 接入账户所有权与选择隔离；消息 presentation 以 nullable `ServerMessageId + ClientMessageId` 区分确认行与本地行，不伪造服务端 ID，并把 pending/failed、重试按钮和滚动策略接入版本化消息视图。
- WPF 增加多行 Text 输入、Enter 发送、Ctrl+Enter 换行、发送状态与失败行重试；只有发送结果证明 pending 已提交时才清除仍未改变的原输入，正文、身份和幂等键不进入本切片新增诊断或对象格式化。

### 验证证据

- `已验证`：最终 `pwsh ./scripts/verify.ps1 -Mode Fast` 与 `-Mode Full` 通过；Debug/Release 构建 0 警告、0 错误，format 与空白检查通过，Shared 35、Server 175、Client 532、Updater 1，共 743/743。
- `已验证`：发送/pending/响应回声/撤权/取消/选择切换关键集每轮 25 项，Release 连续 10 轮 250/250；覆盖 pending 早于 HTTP、201、200 replay、Realtime-first、响应先到、迟到失败不降级、同键重试合流、进程恢复、同进程第二 cache、50 条上限、401、400/403/409/429/5xx、非法 JSON/发送者、稳定撤权与账户终止取消。
- `已验证`：`dotnet ef migrations has-pending-model-changes` 无模型漂移；解决方案 8 个项目无已知 vulnerable package；最终 `git diff --check` 通过。
- `已验证`：真实 Release WPF 主进程取得非零窗口句柄并保持响应，第二实例自动退出，进程清理无残留；XAML Release 编译覆盖输入框、发送按钮、状态和重试事件接线。
- `已验证`：Claude MCP 调用因认证源优先级失败；本机 Opus 与 Sonnet 只读 CLI 均因订阅额度 403 无结论，错误启动的空闲后台会话由主代理判断后停止。失败未冒充审查通过；Codex 固定差异自审补出并修正成功响应发送者校验和同进程 cache 恢复竞态。
- `未验证`：真实账户/VPS/第二客户端端到端发送、网络真实断连后的人工重试视觉、通知联动与 Narrator；按 M5 约束未读取用户提供的 VPS 配置汇总。

### 文件范围

- `src/RelayCove.Client/Storage/`：Text 验证、pending mutation/read/recovery 与脱敏模型。
- `src/RelayCove.Client/Sync/`：发送 HTTP 分类、严格响应校验、单次刷新、协调与撤权收敛。
- `src/RelayCove.Client/Accounts/`、`MainWindow.xaml(.cs)`：runtime/shell、pending presentation/滚动、输入和重试交互。
- `tests/RelayCove.Client.Tests/`：存储、transport/coordinator、shell、presenter 与滚动回归。
- `docs/ai/`：`DEC-035`、任务结果、外层状态与交接证据。

### 决策与限制

- `DEC-035` 冻结 durable pending、单次写请求、显式原键重试、严格发送响应和 nullable 服务端身份；不改变 Shared/Server 协议、SQLite schema/migration、通知编码或依赖。
- 当前只支持 Text；Reply、@、附件、图片/文件、草稿持久化、编辑/撤回、搜索与周期 Sync 均未实现。网络/timeout/429/5xx 不自动重放 POST，必须由用户点击失败行重试。
- 输入清理由完整发送调用返回的 `PendingCommitted=true` 触发，因此慢 POST 期间输入保留并禁用重复提交；该实现优先保证不在落盘前丢输入，未来若需要“落盘即清空、后台继续”必须另行冻结完成通知和认证失效所有权，不在本切片暗改。

### 下一步

- 继续盘点阶段 8 剩余最小闭环，优先选择周期 Sync/重连后的持续收敛或 Reply/@ 中可独立验证的下一纵向切片；真实 VPS/双客户端仍留到 M5 Gate。
