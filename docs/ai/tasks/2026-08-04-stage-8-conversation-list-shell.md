# 阶段 8 账户隔离会话列表与持续状态

## 任务定义

- **任务名称：** 阶段 8 账户隔离会话列表、持续连接/未读与双栏壳
- **状态：** `已完成`
- **基准提交：** `4a48dd4e5e2aa0d6150b850a26fe1e449e937139`
- **工作分支：** `agent/stage-8-conversation-list-shell`
- **相关方案章节：** 9.2–9.4、12.5–12.8、阶段 8；`DEC-017`、`DEC-020`、`DEC-025`、`DEC-026`、`DEC-028`、`DEC-032`

### 目标

把已经权威同步并按账户隔离的本地会话状态接入 production 账户 runtime 和 WPF 双栏主窗口。登录后持续显示真实连接状态、会话级/总未读、最新本地预览与稳定排序；旧账户、未完成权威快照或已撤权数据必须 fail-closed，不能进入 UI。

### 已知事实

- `已验证`：基准 `4a48dd4` 已仅快进到 `agent/v1-integration`；新分支建立时工作树干净，Fast 通过 Shared 35、Server 175、Client 447、Updater 1，共 658/658，Debug 构建 0 警告、0 错误。
- `已验证`：`AccountScopedLocalCache` 已在同一账户数据库和共享 operation gate 下原子维护 `LocalConversations`、`LocalMessages`、权威快照门、撤权 intent/tombstone、单调未读和同步游标，但没有 production 会话列表读取 facade 或变更通知。
- `已验证`：当前 runtime 已拥有 cache、Realtime/Sync/ReadThrough/Notification，实时 sink 能观察有序连接状态、新消息和撤权；账户 coordinator 是 session/runtime/activation lease 的唯一所有者，但 shell 只在 Start/Retry/Logout 边界读取连接状态，总未读仍固定为 0。
- `已验证`：现有 WPF 已有双栏外形和登录/账户切换，账户左栏仍是占位；App 已把 coordinator 快照经 Dispatcher 应用到窗口和托盘。
- `已验证`：Claude #51–#53 均由 0.5 持久只读 job 请求 Opus/XHigh，但终态实际模型均为 `claude-sonnet-5`、`model_mismatch=true`；成立意见已由 Codex 复算、修正并本机验证，三次结果只记第二意见，不冒充 Opus 审查。

### 假设

- `假设`：第一版列表预览只读取本地 `LastMessageId` 精确对应的已缓存消息；快照先于消息页时允许明确显示“暂无本地预览”，不额外发起 History，也不从较旧消息伪造最新预览。
- `假设`：本切片选择会话只更新本地 UI 高亮/导航占位，不写 `ClientActivitySnapshot.OpenConversationId`，因此不会把尚未渲染的消息误判为前台已读；消息视图落地后再原子接入 activity、已读上传和发送。

### 范围

- 必须实现：
  - 在现有 cache gate 下读取不可变会话列表快照；未提交当前 runtime 的 Complete 权威列表、fatal scope 或撤权残留时 fail-closed。
  - 返回会话类型、名称、头像地址、最后消息 ID/时间/本地预览、未读、静音和确定性排序，并以防溢出方式派生真实总未读。
  - 成功数据库提交后、operation gate 外发布不阻塞存储的变更信号；覆盖权威列表、Sync 页、Realtime、撤权和本切片会影响列表/未读的既有路径。
  - runtime/coordinator 在当前账户所有权内转发持续连接和会话状态；注销、切换、取消、Dispose 与迟到读取不得把旧账户状态发布到新 UI。
  - WPF 在 Dispatcher 上应用不可变快照，显示真实总未读和会话列表；选择只更新 UI 高亮，已授权消息/未读通知可选择对应会话或打开列表，不展示消息占位以外的伪造内容，也不提前推进已读。
- 允许修改：
  - `src/RelayCove.Client/Storage/`、`Accounts/`、`Realtime/`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
  - 对应 `tests/RelayCove.Client.Tests/`，以及本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - 消息列表/History/Around、已读推进、输入发送、附件、回复、@、搜索、头像下载和周期 Sync 调度。
  - Shared/Server 协议、SQLite schema/migration、DPAPI 凭据格式、通知激活编码或新依赖。

### 验收标准

- [x] 未完成当前 runtime 的权威会话快照时读取返回明确不可用且不泄露旧磁盘行；Complete 快照成功后列表与总未读正确，撤权/fatal/账户切换保持 fail-closed。
- [x] 列表以 `UpdatedAt DESC, Id ASC` 稳定排序；预览只匹配 `LastMessageId`，空/缺失/非文本类型有明确显示，未读求和不溢出。
- [x] 权威对账、同步、新消息、重复消息、撤权和读状态变化只在成功提交后触发必要刷新；观察者异常、重入读取或慢 UI 不持有 cache gate、不破坏提交或实时 FIFO。
- [x] 持续 SignalR 状态和真实总未读同时更新账户壳与托盘；旧 runtime 的迟到事件/读取在注销、切换或 Dispose 后不能覆盖新账户或登录页。
- [x] WPF 列表只在 UI 线程更新；授权激活可选择会话；没有消息 UI 时选择不写前台会话 activity、不推进已读，并明确保持空详情。
- [x] Fast/Full、状态/竞态/失败定向重复、model drift、八项目漏洞审计、空白检查、真实 Windows 进程 smoke 与独立复核通过；交互式窗口内容捕获和真实登录列表仍按下述限制如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~ConversationList|FullyQualifiedName~ClientAccountShell|FullyQualifiedName~ClientAccountRuntime"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、账户隔离语义、权威快照门或通知激活编码。
- 需要把会话选择等同于已读、增加后台轮询/周期任务或引入新依赖才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4、12.5–12.8、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现账户隔离会话列表、持续连接/未读状态与双栏壳，不实现消息列表或发送。
先确认 cache/runtime/coordinator 的所有权和权威门，再写最小读取与提交后信号。
所有旧账户迟到事件、未完成权威快照和撤权路径必须 fail-closed；UI 集合只在 Dispatcher 更新。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- 最终代码检查点 `ea83e7bf37e83f03c678bf0f82375bfb8a4166af` 增加 production 会话列表读取 facade：只有本进程当前账户已提交 `Complete=true` 权威快照时才返回数据；SQL 以 `(ConversationId, LastMessageId)` 精确连接最后消息，双重排除撤权 tombstone/intent，并在 gate 内再次应用内存 allow/deny 集。列表按 `UpdatedAt DESC, Id ASC` 排序，逐行损坏隔离，busy 映射为 transient，返回真正只读集合和饱和总未读。
- cache 在成功提交并释放 operation gate 后发布单调会话状态信号；fatal 首次转换异步通知。runtime state hub 转发持续连接/会话状态，账户 coordinator 在当前 runtime 所有权内合流刷新、版本化发布列表与壳快照，并在 logout、认证失效、切换和 Dispose 前先退订；旧 runtime 的迟到读取/事件在锁内引用校验失败后丢弃。
- WPF 双栏壳接入虚拟化会话列表、真实名称/类型/预览/时间/静音/未读与总未读；App 只在 Dispatcher 更新控件，托盘只消费同一账户壳的连接+总未读快照。通知目标可待选当前授权会话，Ready 快照中失效的待选 ID 会过期并恢复用户原选择，非活动账户清空待选，避免跨账户复用 GUID。
- 会话选择只改变本地高亮和空详情文案，production activity 的 `OpenConversationId` 仍为 `null`；消息尚未渲染前不推进本地/远端已读。新增空态、标题和未读 live region，不伪造消息内容、History 或发送能力。

### 验证证据

- `已验证`：最终 Fast/Full 均通过；Debug/Release 0 警告、0 错误，Shared 35、Server 175、Client 462、Updater 1，共 673/673；Full 同时通过 format 与空白检查。
- `已验证`：最终会话列表/状态/coordinator/runtime 定向集 41/41；review-fix 后连续 10 轮 410/410。完整 Client 462/462 连续 5 轮，共 2,310 次全绿；此前实现检查点的完整 Client 与定向重复也全绿。
- `已验证`：最终 EF Core `has-pending-model-changes` 报告 model 无变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 对 8 个项目无已知漏洞；`git diff --check` 通过。
- `已验证`：真实 Release WPF 可启动并暴露标题为 `RelayCove` 的响应窗口；重复启动后仍只有一个进程，探针后精确清理且残留进程为 0。`computer-use` 在两次刷新间返回交替失效 HWND，OS 截图又被前台窗口遮挡或为空，因此未把交互式视觉树冒充通过；代码/XAML、Release 构建和真实进程生命周期证据有效。
- `已验证`：Claude #51 job `a478573d-fa39-415d-ab3e-50ffd0c7e60b`（649,211 ms，`$2.9937885`）给出的同步事件/退订/版本和 selection 不推进 activity 建议经 Codex 复算；#52 job `c26a051c-e97e-4182-a465-e2d96e723d22`（804,441 ms，`$4.33353075`）指出待选 ID 失效和 live region；#53 job `959dba5e-1361-4938-8b5a-a0fa2d7fec4e`（1,034,424 ms，`$7.04985325`）对原子未读、损坏行/只读集合、跨账户清理和死锁/迟到发布返回 `PASS`。三次请求均为 Opus/XHigh，但终态实际 `claude-sonnet-5`、`model_mismatch=true`，只作为参考；所有成立项均以最终代码和本机门禁为准。
- `未验证`：没有读取 M5 VPS 配置，也没有真实服务器凭据/第二客户端，因此真实登录后的列表视觉、SignalR 未读变化、通知中心点击定位、托盘数字变化与 Narrator 实际播报保留到后续 UI/M5 Gate。

### 文件范围

- `src/RelayCove.Client/Storage/AccountScopedLocalCache.cs` 与新增会话列表结果/条目类型。
- `src/RelayCove.Client/Accounts/` 的 runtime state hub、持续事件、coordinator、shell 快照和 presentation。
- `src/RelayCove.Client/App.xaml.cs`、`MainWindow.xaml(.cs)`；对应 Client storage/accounts 测试。
- `docs/ai/DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md` 与本任务记录。

### 决策与限制

- 冻结 `DEC-033`：列表可见性继续以当前 runtime 的权威 Complete 快照和账户 scope 为门；读取或 UI 不自行恢复 tombstone，不用旧磁盘行、较旧消息或跨会话相同消息 ID 猜测内容。
- cache 事件只表示“状态可能变化”，不携带敏感 payload；coordinator 以 dirty single-flight 重读不可变快照。每个发布流的单调 revision 只解决同流迟到，不替代 runtime 引用、phase 和 dispose 三重所有权校验。
- 总未读包含静音会话的真实未读，只影响是否提醒的策略仍由通知协调器决定；列表 transient/fatal/未权威状态均清空 UI 与托盘未读，优先 fail-closed。
- 本切片没有消息列表、History/Around、已读推进、发送、头像下载或周期 Sync；会话选择不得写 activity，直到下一切片真实渲染消息后再原子接入 read-through。

### 下一步

- 接入当前会话的虚拟化消息列表、History/Around 与已读推进。
