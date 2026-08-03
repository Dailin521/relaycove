# 阶段 8 账户隔离会话列表与持续状态

## 任务定义

- **任务名称：** 阶段 8 账户隔离会话列表、持续连接/未读与双栏壳
- **状态：** `进行中`
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
- `已验证`：Claude #51 持久只读 challenge 已以 Opus/XHigh 启动，job `a478573d-fa39-415d-ab3e-50ffd0c7e60b`；Claude 只作第二意见，不替代 Codex 的仓库复算和本机验证。

### 假设

- `假设`：第一版列表预览只读取本地 `LastMessageId` 精确对应的已缓存消息；快照先于消息页时允许明确显示“暂无本地预览”，不额外发起 History，也不从较旧消息伪造最新预览。
- `假设`：本切片选择会话只更新当前 activity/导航占位，不推进已读边界；消息加载、已读上传和发送进入后续独立切片。

### 范围

- 必须实现：
  - 在现有 cache gate 下读取不可变会话列表快照；未提交当前 runtime 的 Complete 权威列表、fatal scope 或撤权残留时 fail-closed。
  - 返回会话类型、名称、头像地址、最后消息 ID/时间/本地预览、未读、静音和确定性排序，并以防溢出方式派生真实总未读。
  - 成功数据库提交后、operation gate 外发布不阻塞存储的变更信号；覆盖权威列表、Sync 页、Realtime、撤权和本切片会影响列表/未读的既有路径。
  - runtime/coordinator 在当前账户所有权内转发持续连接和会话状态；注销、切换、取消、Dispose 与迟到读取不得把旧账户状态发布到新 UI。
  - WPF 在 Dispatcher 上应用不可变快照，显示真实总未读和会话列表；选择会话更新 activity，已授权消息/未读通知可选择对应会话或打开列表，不展示消息占位以外的伪造内容。
- 允许修改：
  - `src/RelayCove.Client/Storage/`、`Accounts/`、`Realtime/`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
  - 对应 `tests/RelayCove.Client.Tests/`，以及本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - 消息列表/History/Around、已读推进、输入发送、附件、回复、@、搜索、头像下载和周期 Sync 调度。
  - Shared/Server 协议、SQLite schema/migration、DPAPI 凭据格式、通知激活编码或新依赖。

### 验收标准

- [ ] 未完成当前 runtime 的权威会话快照时读取返回明确不可用且不泄露旧磁盘行；Complete 快照成功后列表与总未读正确，撤权/fatal/账户切换保持 fail-closed。
- [ ] 列表以 `UpdatedAt DESC, Id ASC` 稳定排序；预览只匹配 `LastMessageId`，空/缺失/非文本类型有明确显示，未读求和不溢出。
- [ ] 权威对账、同步、新消息、重复消息、撤权和读状态变化只在成功提交后触发必要刷新；观察者异常、重入读取或慢 UI 不持有 cache gate、不破坏提交或实时 FIFO。
- [ ] 持续 SignalR 状态和真实总未读同时更新账户壳与托盘；旧 runtime 的迟到事件/读取在注销、切换或 Dispose 后不能覆盖新账户或登录页。
- [ ] WPF 列表只在 UI 线程更新；选择项发布当前会话 activity，授权激活选择会话；没有消息 UI 时明确保持空详情，不冒充已读或消息已加载。
- [ ] Fast/Full、状态/竞态/失败定向重复、model drift、八项目漏洞审计、空白检查、真实 Windows smoke 与独立复核通过。

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

- 待完成。

### 验证证据

- 待完成。

### 文件范围

- 待完成。

### 决策与限制

- 待完成。

### 下一步

- 接入当前会话的虚拟化消息列表、History/Around 与已读推进。
