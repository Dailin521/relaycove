# 阶段 8 客户端提及组合与可靠发送

## 任务定义

- **任务名称：** 阶段 8 `@用户` 客户端候选、token 绑定与 durable 发送闭环
- **状态：** `已完成`
- **基准提交：** `0ccc925e99952e900cac9c31d3509b064762ce85`
- **工作分支：** `agent/stage-8-mention-compose`
- **相关方案章节：** 8.3、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-010`、`DEC-017`、`DEC-035`、`DEC-040`

### 目标

让当前 Ready 会话可以显式搜索并插入唯一 `@UserName` token，把仍存在于正文中的已选候选 ID 以最多 20 个、规范排序的不可变集合先落入账户隔离 pending，再通过现有幂等发送/回声/重试路径可靠提交。候选、token、回复和正文必须绑定同一组合上下文；会话切换、撤权、认证失效或迟到结果不得污染新上下文。

### 已知事实

- `已验证`：绿色集成头 `0ccc925` 的 Fast 基线为 807/807；当前分支建立时工作树干净。
- `已验证`：服务端候选 endpoint 只返回会话 ID、`UserId/UserName/DisplayName` 和 `HasMore`，按规范用户名/ID 稳定排序，Public 与 Private/Direct 权限和真实发送授权同构。
- `已验证`：客户端本地 schema、`PendingMessage`、`LocalPendingMessage`、mention 子表和统一 merge 已能持久保存/比较最多 20 个唯一非空提及 ID；retry 会从失败 pending 读取原集合。
- `已验证`：发送服务端先把提及 ID `Order()`，响应/History/Sync/Realtime 也按 ID 稳定投影；客户端发送传输当前硬拒绝非空集合且对响应做顺序相等检查。
- `已验证`：当前 WPF 组合器已有会话/Ready/context version、Reply、正文 ABA 防护和 pending 提交后条件清理，但尚无候选或提及状态。
- `已验证`：Claude #68 XHigh 只读可靠性 challenge 通过当前兼容 MCP 等待后仍因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责设计、实现和本机验证。

### 假设

- `假设`：第一版用显式打开/搜索的 picker，只有用户提交 1–64 位规范用户名字符前缀时才请求服务端，不在每次正文键入时自动联网。
- `假设`：插入 token 固定为 `@UserName`，提及 ID 只在正文仍包含大小写不敏感、两侧用户名边界完整的该 token 时保留；改名/删 token 会立即移除 ID，再次选择可恢复。
- `假设`：候选结果不持久缓存；每次搜索绑定当前 runtime、Ready 会话与 selection version，迟到结果返回 stale/不可用且不展示。

### 范围

- 必须实现：
  - Client 候选 HTTP transport/coordinator/outcome：一次 token refresh、反向代理子路径、严格 URI/状态/响应不变量、稳定撤权处理与全量脱敏。
  - Runtime/Shell 查询入口：只允许当前 Ready selection；请求完成后再次验证 runtime/会话/selection version，拒绝旧账户、切换、撤权或刷新后的迟到结果。
  - 可单测的 token/插入策略：规范 query、用户名字符边界、大小写不敏感精确保留、选择去重和最多 20 个 ID。
  - WPF 显式 picker：查询、候选展示/插入、加载/空/错误/更多提示；切换会话、非 Ready 和成功清空时清除正确的瞬态状态，不显示用户 ID。
  - 扩展 Text 发送链路接收提及集合；在 pending 落盘前校验并按 GUID 规范排序，HTTP/Realtime/Sync/History 继续使用同一不可变集合，失败重试精确复用。
  - 组合提交捕获正文、回复、提及集合、会话与 context version；只有 pending 已提交且所有捕获上下文仍相同时才清空，避免迟到完成覆盖用户新编辑。
- 允许修改：
  - Client Sync/Accounts/Storage 使用点、`MainWindow.xaml(.cs)`、Client 测试和必要 `docs/ai/` 记录。
- 明确不做：
  - 服务端/Shared 协议变化、schema/migration、新依赖、昵称搜索、自动正文键入请求、富文本高亮、头像/在线状态、通知专门样式、附件、全局目录、VPS/双客户端实测。

### 验收标准

- [x] 候选 GET 在真实 HTTP handler 下验证 auth refresh、子路径、query escaping、响应形状/排序/前缀/上限、状态分类、取消、日志脱敏和撤权清理。
- [x] shell/runtime 只接受当前 Ready 会话，切换账户/会话、selection revision 变化或撤权后的迟到候选不会发布到新组合器。
- [x] token policy 对开头/中间、标点、邮箱样式、相邻用户名字符、大小写、删除/编辑、重复候选和 20 上限有确定测试。
- [x] 非空提及 ID 在 HTTP 前先落盘且规范排序；一次 refresh、响应/Realtime 竞态、失败/重启/显式重试均保留完全相同集合，非法集合不落盘不联网。
- [x] WPF picker 可访问、不会阻塞 UI；会话/Ready/发送上下文清理与 Reply/正文 ABA 防护共存；Fast/两次 Full、定向重复、真实 Release WPF smoke、model drift、八项目漏洞审计与空白检查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Mention|FullyQualifiedName~ClientMessageSendCoordinatorTests|FullyQualifiedName~ClientAccountShellCoordinatorTests"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要改服务端/Shared 冻结协议、数据库、加入新依赖、开放全局/昵称搜索，或无法在 pending 前冻结规范提及集合。
- 无法把候选结果和提交清理绑定到当前账户/会话/context，或真实 WPF smoke 无法证明窗口响应与单实例清理。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 8.3/10.4/12.1–12.3/12.6–12.8/阶段 8、docs/ai/STATUS.md 和本任务。
只实现客户端会话作用域 picker、token→ID 绑定和 durable 非空提及发送；不改服务端/Shared/schema。
候选与发送必须绑定当前 Ready 会话；所有 ID 在 pending 前校验、去重并稳定排序，retry 只能复用原集合。
不把 query、用户名、昵称、正文或提及 ID 写入日志/错误/ToString；迟到结果和提交不得覆盖新组合上下文。
```

## 任务结果

### 修改摘要

- 新增会话作用域候选 HTTP transport/coordinator，包含反向代理子路径、一次 refresh、严格状态分类、响应排序/前缀/字段/上限校验、撤权清理、取消和全量脱敏。
- 将候选查询接入账户 runtime 与 shell 的当前 Ready selection 门；请求完成后重检 runtime、会话和 selection revision，旧结果 fail-closed。
- 新增确定性的用户名/token/插入/ID 快照策略，并接入显式 WPF picker；候选与已选状态随会话、Ready 和账户边界清理，不展示内部 ID。
- 扩展 Text durable 发送链：pending 前校验并规范排序最多 20 个唯一非空 ID，HTTP 响应逐项匹配，重启、失败与显式 retry 精确复用原集合。
- 组合提交捕获正文、Reply、提及集合、会话和 context version；只有 durable 提交成功且上下文未变化才清空，覆盖正文编辑与 ABA 竞态。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 807/807；Shared 37、Server 192、Client 577、Updater 1。 |
| `已验证` | 最终 Fast 与三次 Full | 固定代码提交 `82c083d5fa319ea2cb2fe8fc51ba09c5382c8a1c` 均为 860/860；Shared 37、Server 192、Client 630、Updater 1；Release 0 警告、0 错误，format 与 `git diff --check` 通过。 |
| `已验证` | Release 提及/发送/shell 关键集连续 10 轮 | 每轮 74 项，共 740/740；覆盖真实 HTTP handler、token 边界、selection 切换/认证失效、pending-before-HTTP、响应乱序、重启恢复和原集合 retry。 |
| `已验证` | EF 与依赖安全 | `has-pending-model-changes` 无漂移；八项目直接与传递依赖无已知漏洞；敏感 logger 检索无命中。 |
| `已验证` | Release WPF 生命周期 smoke | 主实例 PID 22924 有响应且有窗口句柄；第二实例 PID 47800 正常退出，运行中匹配实例数为 1；按精确 PID 清理后为 0。 |
| `未验证` | 登录态 picker 可视/UIA | 本机无登录会话时账户面板按设计为 `Collapsed`，补充 UIA 探测不适用且未冒充通过；Release XAML 编译、显式 AutomationProperties 和策略/协调器自动化已通过，真实登录视觉、键盘、Narrator 留到 M5。 |
| `已验证` | Claude #68 XHigh 只读 challenge | 当前 Desktop 只暴露兼容调用，等待后仍因本机认证源优先级失败；无 job、模型、workspace、费用或结论，未将其计为审查通过。 |

### 文件范围

- 新增：`ClientMentionPolicy`/`ClientMentionTextEdit`、候选 transport/coordinator/outcome/status 及对应测试、本任务记录。
- 修改：账户 runtime/factory/interface/shell、WPF 主窗口、账户隔离 cache、Text send coordinator/transport 及对应账户与发送测试、状态/执行/决策记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-041` — 正文中的完整 `@UserName` token 是可见意图，最多 20 个规范排序 ID 是 durable 协议载荷；候选和提交均绑定当前 selection/context，retry 只复用落盘原集合。
- 已知限制：显式用户名搜索，不提供昵称/模糊/全局搜索或富文本高亮；真实跨机体验保留到 M5。

### 下一步

- 仅快进集成该切片；阶段 8 功能条目完成后进入阶段 9 附件最小纵向闭环。
