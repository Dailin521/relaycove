# 阶段 8 有界消息列表、History/Around 与渲染后读穿

## 任务定义

- **任务名称：** 阶段 8 当前会话消息列表、本地首屏、History/Around 与渲染后读穿
- **状态：** `已完成`
- **基准提交：** `a477d68d8477b6d3bee938b29ff22a3cfcb7fa05`
- **工作分支：** `agent/stage-8-message-list-shell`
- **相关方案章节：** 9.2–9.4、10.4、12.3、12.6–12.8、阶段 8；`DEC-017`、`DEC-020`、`DEC-026`、`DEC-027`、`DEC-030`、`DEC-033`

### 目标

把当前已授权会话接入真实、虚拟化且有界的消息视图：普通选择先显示本地最新窗口并可向上懒加载 History，消息通知在本地缺失目标时经 Around 定位。只有当前账户/当前选择的消息快照真正应用到 WPF 后，才把该会话登记为打开并推进本地 read-through；切换、注销和撤权必须 fail-closed。

### 已知事实

- `已验证`：基准 `a477d68` 已仅快进并推送到 `agent/v1-integration`；新分支建立时工作树干净，Fast 通过 Shared 35、Server 175、Client 462、Updater 1，共 673/673，Debug 构建 0 警告、0 错误。
- `已验证`：Shared/Server 已实现并测试 History 的排除式 `beforeMessageId` keyset、升序响应和 Around 双侧窗口；稳定撤权 `403 ConversationAccessRevoked` 是可触发本地清理的权威信号。
- `已验证`：客户端 cache 已有账户/权威快照/deny-set 门、统一 Realtime/Sync/History/SendResponse 合并、read-through pending/安全上传及提交后状态信号；现有 `ReadMessagesAsync` 会读取全部已缓存服务端消息，不适合作为 production 虚拟化首屏接口。
- `已验证`：账户 coordinator 已拥有当前 runtime 的订阅、版本化发布和旧 runtime 隔离；WPF 会话选择当前只改变高亮，`OpenConversationId` 始终为 `null`，因此尚未把未渲染消息误判为已读。
- `已验证`：通知激活目标同时携带账户 scope、会话 ID 和消息 ID，现有授权路由只会把当前 scope 中可访问的目标交给 UI。

### 假设

- `假设`：首屏和每次 History 页固定为最多 50 条；本地读取与网络页都保持服务端消息 ID 严格升序，较旧页只前插，不把正在查看历史的用户强制滚到底。
- `假设`：普通选择可在本地首屏应用后异步补取最新 History；通知目标优先使用本地消息，缺失时调用 Around。Around 是定位入口，不替代向上 History 的连续分页状态。
- `假设`：本切片只显示服务端已确认的 Text/System 等现有 `MessageDto`；pending 发送行留到发送切片统一接入，不能伪造成已发送消息。

### 范围

- 必须实现：
  - 在 cache gate 与当前权威/撤权门内提供有界、不可变、稳定排序的本地消息窗口；逐行/页面损坏、busy、fatal 和撤权有明确失败状态，不把全库消息直接交给 UI。
  - 增加账户作用域 History/Around HTTP 读取与严格响应校验；每个远端页面通过现有唯一合并语义原子写入，History 不更新会话预览、不推进 Sync cursor、不产生未读/Toast/声音。
  - 对同一当前选择实行取消、generation 和 single-flight/dirty 合流；旧账户、旧会话、迟到 HTTP/缓存读取、撤权和注销不得发布消息或恢复 activity。
  - 发布版本化、不可变的消息视图快照，支持本地首屏、向上加载更多、通知目标定位和明确的 loading/empty/transient/fatal/revoked 状态。
  - WPF 在 Dispatcher 上应用虚拟化消息集合，保留历史阅读位置；快照实际应用后回执 coordinator，只有仍为当前账户/选择/版本时才设置 `OpenConversationId`、单调标记已渲染边界并触发既有 read-through 上传。
- 允许修改：
  - `src/RelayCove.Client/Storage/`、`Sync/`、`Accounts/`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
  - 对应 `tests/RelayCove.Client.Tests/`，以及本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - 消息发送、输入框、失败重试、回复、@、链接/复制、日期/新消息分割线、搜索、附件、头像下载和周期 Sync 调度。
  - Shared/Server 协议、SQLite schema/migration、DPAPI/通知激活编码或新依赖。

### 验收标准

- [x] 本地首屏和 older window 均最多 50 条、严格升序、去重且真正只读；大量历史不会被一次性读入 UI，未权威/fatal/撤权/损坏状态不泄露旧数据。
- [x] History/Around 对 URI、页边界、会话归属、目标归属、顺序、数量、continuation/has-more 组合做防御校验；页面任一冲突或错误不留下部分合并，稳定撤权 `403` 收敛到 tombstone/通知清理。
- [x] 快速切换会话、账户切换、注销、撤权、重入加载和迟到结果不能覆盖当前视图；同一方向重复加载合流，不死锁 cache/runtime/UI。
- [x] 消息列表启用 WPF recycling virtualization；旧页前插保持用户位置，新 Realtime 只有用户位于最新区域时跟随到底，否则保留位置并显示新消息提示。
- [x] 只有当前版本消息快照已由 Dispatcher 应用后才设置 activity 和推进 read-through；仅选择、加载中、失败、空/失效目标和旧版本回执均不标记已读。
- [x] Fast/Full、消息 cache/History/Around/coordinator/WPF 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；无真实账户/VPS/第二客户端的场景如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~MessageList|FullyQualifiedName~MessageHistory|FullyQualifiedName~MessageAround|FullyQualifiedName~ClientAccountShell"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、账户隔离/权威快照/撤权语义或通知激活编码。
- 需要把“选中”本身等同于已读、允许旧选择后台结果改变 activity，或引入新依赖才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4、10.4、12.3、12.6–12.8、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现有界消息视图、History/Around 懒加载和渲染后 read-through，不实现发送、输入、搜索或附件。
先确认 cache 的原子合并/撤权门、runtime 所有权和 Dispatcher 回执，再写最小读取、网络与发布路径。
所有旧账户/旧选择迟到结果、未完成权威快照和撤权路径必须 fail-closed；UI 集合只在 Dispatcher 更新。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- 最终代码检查点 `46a59f6482263cdbf9a1d12a1470aa79bdff6960`（主体 `51b6502`，审查修复 `46a59f6`）增加账户作用域有界消息页面：每页最多 50 条、排除式 keyset、deferred 只读事务、严格升序与只读结果；未权威、busy、损坏、fatal 和撤权均有明确状态，非 Ready 发布不携带旧消息、分页或目标。
- 新增账户作用域 History/Around HTTP transport 与 coordinator，严格校验 URI、会话/目标归属、顺序、数量和 has-more 组合；整页复用唯一合并语义并在单一事务提交，History 不改预览/Sync cursor、不发通知，首次新行保持未读直到真实渲染，稳定撤权复用 tombstone/通知清理。
- coordinator 以当前 runtime subscription、selection generation、取消和 single-flight/dirty 合流隔离迟到结果；WPF 在 Dispatcher 应用不可变快照，使用 recycling virtualization、前插 extent 补偿和新消息提示。等价窗口重发不替换 `ItemsSource`，滚动回执只接受已应用 revision；非 Ready/离开最新区域立即清 activity，只有当前已应用且前台最新视口才提交精确渲染边界。
- 认证失效直接收口账户会话，写入 transient 不紧循环；快照/outcome `ToString()` 保持消息、身份和目标脱敏。未增加 schema、migration、Shared/Server 协议或依赖。

### 验证证据

- `已验证`：最终 Fast/Full 均通过；Debug/Release 构建 0 警告、0 错误，Shared 35、Server 175、Client 493、Updater 1，共 704/704；Full 同时通过 format 与 `git diff --check`。
- `已验证`：最终 cache/History/Around/coordinator/滚动关键集 81/81，Release 连续 10 轮共 810/810；额外定向 42/42 和完整 Client 493/493 通过。失败状态清空旧消息/activity、尚未应用 revision 不推进 read-through、重复窗口保留 offset 均有回归。
- `已验证`：EF Core `has-pending-model-changes` 报告 model 无变化；`dotnet list RelayCove.sln package --vulnerable --include-transitive` 对 8 个项目无已知漏洞。
- `已验证`：真实 Release WPF 枚举到唯一可见标题 `RelayCove` 的响应窗口；第二次启动退出码 0 且只保留一个进程，探针后精确清理为 0。
- `已验证`：Claude #54 job `b0d5a420-dafa-4ed3-9d03-15bd2971df62` 完成 challenge（673,773 ms，`$2.8707420000000003`），请求 Opus/XHigh、实际 `claude-sonnet-5`、`model_mismatch=true`；成立项由 Codex 复算并修正。#55 错误 workspace 后主动取消且费用 unavailable；#56 job `090a3bc5-29fc-4cf6-a349-3da279083645` 在 1,666,134 ms 后因订阅额度 403/CLI code 1 失败（`$11.044499750000004`），无正式答案；失败前两条部分意见经本地复算成立并在 `46a59f6` 修正，未把失败冒充审查通过。
- `未验证`：没有读取 M5 VPS 配置，也没有真实服务器凭据/第二客户端，因此真实登录后的消息视觉、SignalR 到达/History/Around、通知点击定位、端到端 read-through 和 Narrator 播报保留到后续 UI/M5 Gate。

### 文件范围

- `src/RelayCove.Client/Storage/` 的有界页面、History 原子提交和渲染边界；`Sync/` 的 History/Around transport、校验与协调器。
- `src/RelayCove.Client/Accounts/` 的 runtime facade、selection 状态机、消息快照/presenter/滚动策略；`App.xaml.cs`、`MainWindow.xaml(.cs)` 的 Dispatcher/虚拟化 UI。
- 对应 `tests/RelayCove.Client.Tests/Storage/`、`Sync/`、`Accounts/` 回归；`docs/ai/DECISIONS.md`、`STATUS.md`、`V1_EXECUTION.md` 与本任务记录。

### 决策与限制

- 冻结 `DEC-034`：published、Dispatcher-applied 与 viewport 三种状态必须分离；revision 只解决同一流顺序，不能替代当前 runtime/selection 所有权。非 Ready 快照一律空消息并清 rendered activity。
- History/Around 只合并服务端已确认消息；History 首次新行在渲染前保持未读，但不产生通知候选/声音、不更新会话预览。渲染回执是本切片唯一新增的已读推进入口，当前既有 Realtime 前台规则只在此前已应用的最新视口成立时继续生效。
- 本切片不增加消息表索引或 migration；当前查询有界且门禁通过，若真实大历史 profiling 证明需要索引，必须另开 schema/migration 任务。输入、Text 发送、pending/失败重试、回复、搜索和附件仍未实现。

### 下一步

- 完成本切片后进入消息输入与 Text 发送闭环。
