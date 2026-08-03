# 阶段 8 新消息分割线

## 任务定义

- **任务名称：** 阶段 8 打开会话时的稳定新消息边界与分割线
- **状态：** `已完成`
- **基准提交：** `4b201b656f1e7675fb397ee609cdf84b35247279`
- **工作分支：** `agent/stage-8-new-message-divider`
- **相关方案章节：** 9.2–9.4、12.3、12.6–12.8、阶段 8；`DEC-026`、`DEC-027`、`DEC-034`

### 目标

用户打开含未读消息的当前授权会话后，在已加载且可证明精确的第一条他人未读消息前显示一次“新消息”分割线。分割线使用打开时冻结的本地已读边界，不因首屏渲染后的 read-through 推进而移动或消失；切换、撤权、非 Ready 或新 selection 必须清除旧边界。

### 已知事实

- `已验证`：绿色集成头 `4b201b6` 的 Fast 基线为 782/782；当前分支建立时工作树干净。
- `已验证`：当前首屏消息应用后，WPF 会以已应用 revision 和最新区域回执触发 `MarkConversationRenderedThroughAsync`；若临时读取当前未读状态，分割线会在首屏读穿后错误消失。
- `已验证`：`ReadMessagePage` 已在账户权威门内使用 deferred SQLite 只读事务读取当前消息页，但尚未把同事务的 `LastReadMessageId` / `UnreadCount` 带给 selection。
- `已验证`：首屏本地 50 条不保证已经覆盖服务端最早未读；History/Around 的 `HasMoreBefore` 与最老已加载 ID 可证明加载窗口是否已跨过冻结已读边界。
- `已验证`：pending 行无服务器身份且自己的确认消息不增加未读，均不得成为“新消息”分割线目标。
- `已验证`：Claude #66 MCP 只读可靠性 challenge 因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责设计、自审和本机验证。

### 假设

- `假设`：分割线在一次 message selection 生命周期内保持稳定，即使用户已读并完成 read-through；离开并重新打开会话时按新的原子本地状态重新计算。
- `假设`：只有打开时 `UnreadCount > 0` 才建立候选；目标为已加载的首条 `Id > frozen LastReadMessageId` 的他人确认消息。
- `假设`：当 `HasMoreBefore=true` 且当前最老确认消息仍高于冻结边界时，最早未读尚不可证明，暂不显示近似分割线；加载更早页跨过边界后再显示。

### 范围

- 必须实现：
  - 在当前账户/会话权威门和同一个本地只读事务中，把非负 `LastReadMessageId` 与 `UnreadCount` 和消息页一起返回，失败结果不暴露陈旧边界。
  - 初始 selection 只冻结一次边界；History/Around 完成后根据分页事实单调判定边界是否已解析，refresh、读穿和旧异步结果不得移动或复活它。
  - presentation 只在精确目标前设置一次 `ShowNewMessageSeparator`，不标记 pending/自己的消息，并保持 `ToString()` 脱敏。
  - WPF 显示可访问的“新消息”分割线；非 Ready、切换、撤权和账户终止隐藏旧内容。
  - 覆盖原子页状态、零未读、自己的消息、分页未解析/跨界解析、History/Around、read-through 后稳定、切换/迟到结果与 WPF smoke。
- 允许修改：
  - Client 本地消息页 outcome/read、账户 shell selection/presentation、`MainWindow.xaml` 与对应 Client 测试；必要的 `docs/ai/` 记录。
- 明确不做：
  - 修改 SQLite schema/migration、Shared/Server API、未读计数算法、read-through 上传、自动滚动/新消息按钮、`@用户`、通知、VPS 或外部依赖。

### 验收标准

- [x] 成功页在同一只读事务返回有效的已读/未读状态；非 Ready/撤权/失败不带可显示边界，日志与 `ToString()` 不泄露 ID 或内容。
- [x] 每个 Ready selection 最多一个分割线，只位于冻结边界后的首条他人确认消息前；零未读、pending、自己的消息和未解析分页不显示。
- [x] 首屏 read-through、refresh、加载更早页、Around、账户/会话切换、非 Ready 和迟到回调均保持稳定或 fail-closed。
- [x] Fast/Full、cache/shell/presenter 定向与重复、model drift、八项目漏洞审计、空白检查和真实 Windows WPF smoke 通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~MessagePage|FullyQualifiedName~ClientAccountShellCoordinator|FullyQualifiedName~ClientMessageListPresenter"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要改变服务端已读定义、未读持久化/schema、Shared 协议，或无法从当前本地事务与分页事实证明精确边界。
- 需要让 UI 在未解析时显示猜测位置，或让历史加载/read-through 回退已读边界。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4/12.3/12.6–12.8/阶段 8、docs/ai/STATUS.md 和本任务。
只实现打开时冻结且可证明精确的新消息分割线；不改变未读、read-through、协议或 schema。
先验证本地页事务、selection generation 和 History/Around 分页事实，再实现 presentation/WPF。
旧账户、旧会话、非 Ready 和迟到结果必须 fail-closed；日志与 ToString 保持脱敏。
```

## 任务结果

### 修改摘要

- 本地消息页在既有 deferred SQLite 只读事务中一并读取并校验 `LastReadMessageId` / `UnreadCount`，成功 outcome 携带原子状态，失败与 `ToString()` 不暴露可复用边界。
- message selection 在第一次成功最新页读取时冻结状态；History 与 Around 分别按连续页事实单调证明精确边界，refresh 与渲染后的 read-through 不覆盖冻结值。
- presentation 只在冻结边界后的首条他人确认消息前设置一次标记，pending、自己的消息、零未读和未解析分页保持隐藏；WPF 虚拟化行内新增可访问“新消息”分割线。
- 接受 `DEC-039`，明确宁可暂不显示也不显示分页缺口下的近似位置。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 782/782；Shared 35、Server 175、Client 571、Updater 1。 |
| `已验证` | 最终 Fast / 两次 Full | 同一代码树提交前与固定代码提交 `49d72a3f882c2ea450010bec83a0d18e30a02d26` 上均通过；Release 0 警告/0 错误、format、Shared 35 + Server 175 + Client 577 + Updater 1 = 788/788，`git diff --check` 通过。 |
| `已验证` | cache/shell/presenter Release 关键集 | 每轮 52 项，连续 10 轮共 520/520；覆盖事务页状态、读穿后刷新稳定、分页延迟/跨界解析、Around 精确/未解析、本人/pending 排除与脱敏。 |
| `已验证` | EF model drift 与 NuGet 漏洞 | EF Core 无 pending model changes；解决方案 8 个 source/test 项目含传递依赖均无已知漏洞。 |
| `已验证` | 真实 Release WPF smoke | 主进程 PID 26076、非零窗口句柄 27856744、`Responding=True`；第二实例 PID 56588 退出码 0、同路径运行中仅 1 个实例，精确 PID 清理后为 0。Release XAML 编译覆盖新分割线绑定。 |
| `未验证` | 真实登录数据下的分割线视觉 / Narrator / VPS / 双客户端 | 空登录壳 smoke 不冒充真实消息视觉；保留到 M5 Gate，本任务未读取 VPS 配置。 |
| `未验证` | Claude #66 独立可靠性 challenge | MCP 因认证源优先级失败，无 job、模型、workspace、费用或结论；不冒充通过，Codex 固定差异与本机门禁为依据。 |

### 文件范围

- 新增：本任务记录。
- 修改：本地消息页 outcome/cache、账户 shell selection、消息 presentation、`MainWindow.xaml`、三组 Client 测试及状态/执行/决策记录。
- 删除：无。

### 决策与限制

- 决策：接受 `DEC-039`，冻结 selection 初始未读状态并只在分页事实证明精确后显示唯一分割线。
- 已知限制：边界未被当前加载窗口证明精确时不显示近似分割线；真实 VPS/双客户端与 Narrator 留到 M5。

### 下一步

- 将完成提交仅快进到 `agent/v1-integration`，随后冻结并实现阶段 8 `@用户` 所需的最小普通用户目录/提及闭环。
