# 阶段 10 客户端权限化搜索、重新授权跳转与短暂高亮

## 任务定义

- **任务名称：** 阶段 10 current/global 搜索客户端闭环
- **状态：** `进行中`
- **基准提交：** `883e55afb8bf150b03f2d20719a215632693b09f`
- **工作分支：** `agent/stage-10-search-ui`
- **相关方案章节：** 9.2–9.4、12.8、15、阶段 10、21.4；`DEC-012/017/018/025/032/033/034/052`

### 目标

在现有 production 账户壳中交付可运行的显式搜索 UI：认证用户可搜索全部会话或当前 Ready 会话的中文/Unicode 消息正文和附件原名，看到有界结果，点击后先经服务端 Around 重新授权，再打开会话、拉取上下文、滚动到原消息并短暂高亮。结果、请求和高亮必须绑定当前 runtime 与单调 generation；注销、撤权、A→B→A、会话切换或迟到回调不能把旧内容提交到新上下文。

### 已知事实

- `已验证`：绿色集成基线、当前 HEAD 与远端 `agent/v1-integration` 均为 `883e55a`；搜索 API production `4c4edba` 已通过最终 Fast/Full 1,378/1,378、Search 30/30、两路复审、model drift 与漏洞审计。
- `已验证`：服务端协议是 `GET /api/search?keyword=...&conversationId=optional&limit=50`；省略 conversation 表示全局，指定不可见会话稳定 403，结果按 Message ID 降序且最多 50 项。
- `已验证`：Client 已有 Bearer + 一次 refresh + 有界 JSON transport 范式；runtime/factory 已组合 history、mention、send 等 coordinator；shell 的 `RuntimeSubscription` 对象身份和 `MessageSelection` token 是现有 A→B→A 与迟到结果提交门。
- `已验证`：`SelectConversation(conversationId, targetMessageId)` 会先发布本地消息，只有本地缺目标才调用 Around。旧搜索结果在服务端已撤权而本地事件未到的窗口内不能直接复用该同步入口，否则可能短暂显示旧缓存。
- `已验证`：Around 已执行当前服务端授权、稳定 403 durable revoke、协议校验和原子缓存合并；WPF 已依据 `TargetMessageId` 做一次滚动，但当前没有搜索 UI 或视觉高亮。
- `已验证`：三路 Codex 只读调查/挑战已完成。可靠性挑战要求 runtime subscription + request serial、结果对象 identity lease、点击前无条件 Around、撤权即时清结果和 recycling-safe 高亮。
- `已记录`：本决策唯一一次 Claude #80 已以本机 Claude Code 2.1.221 后台持久 Sonnet/High 只读任务 `213daa77` 启动，工具限于 Read/Glob/Grep；主线不等待其阻塞，完成后读取并由 Codex 本地裁定。

### 已冻结客户端边界

- 新增 `ClientSearchPolicy`，与服务端一致地 trim 并验证 1–64 个有效 Unicode scalar、至少一个非空白、拒绝 Control/无效 UTF-16；只在用户点击或 Enter 时联网，不做 typeahead、自动重试或本地搜索。
- transport 使用账户 canonical base URI 和当前认证 session，GET query 逐项转义；401 只 refresh 一次，稳定 scoped 403 与普通 403 分离，429 单独映射并只接受有界 `Retry-After`，I/O/timeout 与调用方取消分离。成功/error body 均有字节上限，日志不得包含 URI query、keyword、result、显示名、文件名或 ID。
- coordinator 验证结果非空字段/Unicode/长度、唯一 Message ID、严格降序、数量不超 limit、`HasMore` 满页语义，以及 scoped 结果全部属于请求会话；稳定 scoped 403 复用现有 durable revoke 与通知清理。outcome/status/展示 record 的 `ToString()` 全量脱敏。
- shell 的每次搜索捕获 exact `RuntimeSubscription`、单调 request serial、scope 和 current `MessageSelection`（若适用）。新搜索立即使旧 lease 失效；Current 必须有 Ready selection 且选择变化后返回 `Stale`，Global 始终传 null 且不绑定当前选择。HTTP 返回只能在同一锁内通过 runtime/subscription/serial/scope 和当前权威 Ready 会话列表后提交。
- shell 只认可当前已提交结果集合中的 DTO **对象身份**作为 navigation lease。任一当前 runtime `ConversationStateChanged` 先同步递增 serial、取消 flight、清 active results/highlight，再继续现有异步列表刷新；宁可新消息使搜索失效，也不让撤权内容滞留。
- 新增异步 `NavigateSearchResultAsync`：目标即使已在本地，也必须先对 exact `(conversationId,messageId)` 调用一次 Around；只有 Completed、目标/会话一致、缓存提交成功且 post-await 仍为同一 runtime/subscription/request/result、会话仍在权威列表时，才发布 selection。401 结束账户；稳定 403 durable revoke；429/取消/普通 403/协议错误/迟到结果均零本地内容发布。已验证 Around outcome 可一次性交给新 selection，避免为了打开同一目标重复请求。
- WPF 在右侧会话区提供显式关键词、Global/Current scope、搜索/关闭、通用 live status 和可键盘访问的结果列表。每项展示会话、发送者、时间、snippet 和可选“匹配附件”；附件-only 结果不得为空行。结果列表本身不是 live region，UIA 只暴露当前可见文本，不附加隐藏 keyword 或内部 ID；失败、新输入/scope、新搜索、注销、撤权和 runtime 替换实际清空 ItemsSource。
- 搜索点击建立 exact conversation/message/navigation generation 的 UI-only highlight lease。`ScrollIntoView` 后必须等 recycling 容器真实生成并再次核对 DataContext/可见范围，才显示约 2 秒高亮并确认目标已应用；普通刷新不得重复消费。新导航、会话/runtime 变化、撤权、容器 recycling、窗口关闭或有界 materialize 失败立即清理，高亮不进入 SQLite、snapshot 或服务端。

### 范围

- 必须实现：
  - Client search policy、HTTP transport/coordinator、严格响应校验和结果/状态模型。
  - runtime/factory/shell 接线、latest-wins lease、撤权清理和 Around-first 安全导航。
  - current/global WPF 搜索条、结果/空/忙/错状态、附件名展示、键盘与无障碍。
  - 结果点击、Around 上下文、一次性滚动/高亮和真实 WPF STA 自动化。
- 允许修改：
  - `src/RelayCove.Client/Search/`、`Sync/`、`Accounts/`、`MainWindow.xaml`、`MainWindow.xaml.cs`
  - 对应 Client tests 与必要的 `docs/ai/` 记录。
- 明确不做：
  - Shared/Server/API/schema/migration、FTS/ICU、生产依赖或服务端限流变化。
  - 本地缓存搜索、typeahead、相关性、cursor、保存最近搜索、正文富文本命中标记。
  - 更新器/安装包/部署、VPS、双客户端、真实登录视觉与 Narrator Gate。

### 验收标准

- [ ] Global/Current 显式搜索使用准确 scope；中文完整/部分词、附件名和结果字段可展示，附件-only 行可理解，空/HasMore/429/错误状态明确。
- [ ] 401 refresh 恰好一次；稳定/普通 403、429、timeout、取消、协议错误分离；payload/字段/顺序/唯一性严格验证且所有日志/`ToString()` 脱敏。
- [ ] query1/query2 乱序、current A→B→A、同 AccountScopeId 的 runtime A→B→A、忽略取消 handler 均不能提交旧结果。
- [ ] 结果显示后任一 ConversationStateChanged、撤权、注销或 runtime 替换立即清空；Current 选择变化 stale，Global 不依赖当前选择。
- [ ] 目标已在本地也先 Around；Around 成功前零选择/缓存展示，稳定 403 durable revoke，其他失败零导航；成功后只请求一次 Around 并精确定位。
- [ ] recycling 容器真实生成后只高亮一次，约 2 秒后恢复；刷新/回收/新导航/撤权/A→B→A 不复用旧高亮，生成失败不声明成功或推进目标已读。
- [ ] Client 定向、Fast、最终 Full、model drift、依赖漏洞、空白检查、两路独立 Codex 复核和真实 Release WPF lifecycle/搜索控件 smoke 通过；没有 schema、依赖或无关改动。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Search"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须放宽现有 Public/Private/Direct 权限、绕过 Around 当前授权、在撤权后展示缓存或把搜索结果持久化才能完成。
- 必须改变 Shared/Server 协议、数据库、引入新依赖，或无法用 exact runtime/request/result identity 封闭迟到结果。
- 无法在 WPF recycling 虚拟化下证明高亮绑定目标容器，或必须为视觉效果提前推进 read-through。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS、方案 15/阶段10/21.4、DEC-012/017/018/025/032/033/034/052、STATUS 和本任务。
只实现 Client 权限化显式搜索闭环；不改 Shared/Server/schema/deps，不做 typeahead/本地搜索。
结果提交绑定 RuntimeSubscription 对象 + request serial；current 再绑定 exact selection。
旧结果点击必须 Around-first 重新授权，成功前零本地发布；DTO 对象身份是 navigation lease。
ConversationStateChanged、注销、runtime 变化立即清结果；高亮只在真实目标容器上一次性显示。
Claude #80 只读后台运行；普通实现/审查使用 Codex，任何意见仍由主代理本地验证。
```

## 任务结果

`进行中`。实现、独立复核与最终验证完成后填写生产提交、证据、限制和下一步。
