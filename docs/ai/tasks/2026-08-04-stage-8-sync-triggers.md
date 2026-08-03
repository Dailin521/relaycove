# 阶段 8 窗口激活与周期 Sync 持续收敛

## 任务定义

- **任务名称：** 阶段 8 WindowActivated / Periodic Sync 自动触发与账户生命周期收敛
- **状态：** `已完成`
- **基准提交：** `6d2d2057757267e6a1181eb149d9f5dd375df2da`
- **工作分支：** `agent/stage-8-sync-triggers`
- **相关方案章节：** 4.2、9.2–9.4、12.4–12.8、21.2、阶段 8；`DEC-021`、`DEC-025`、`DEC-028`、`DEC-032`、`DEC-033`、`DEC-034`

### 目标

把既有单账户 Sync 协调器接到真实窗口激活和账户存活期间的周期触发，使 SignalR 推送丢失、设备短时离线或后台错过事件时仍能自动收敛。自动触发必须有界、可停止且服从当前账户所有权；重复窗口事件、长同步、注销和切换账户不得产生并发循环、迟到旧账户触发或通知策略错配。

### 已知事实

- `已验证`：基准 `6d2d205` 是已推送的绿色集成头，包含已完成 Text 发送切片；分支建立时工作树干净。
- `已验证`：`ClientSyncCoordinator` 已实现账户级 single-flight、运行中原因合并和至多一次补跑，优先级为 `WindowActivated > 未完成 Startup > Reconnect > Periodic`；账户 Dispose 才取消共享同步循环。
- `已验证`：Realtime 已在自动重连成功、未知会话和丢失事件时通过 `ClientAccountSyncRequestor` 请求 `Reconnect`；`ClientAccountRuntime.TriggerSyncAsync` 已支持启动完成后的显式 `WindowActivated/Periodic`，但 production 没有真实窗口或定时钩子。
- `已验证`：WPF 把窗口可见、最小化和激活状态经 shell 发布到 runtime；账户创建前会缓冲 activity，Startup 完成并取得 runtime 所有权后重放最新 activity。
- `已验证`：通知协调器已经覆盖 `WindowActivated=None`、前后台 `Reconnect/Periodic` 和 Recovery 规则；工程方案要求 Periodic 补偿推送失败，但没有规定时间间隔。
- `已验证`：Claude #61 只读可靠性 challenge 因本机认证源优先级冲突失败，无 job、模型、费用或结论；本任务继续由 Codex 以仓库证据和本机验证负责。

### 假设

- `假设`：默认周期为 5 分钟；每次自动请求观察结束后再等待下一周期，不为长同步累积 timer tick。该数值只影响客户端本地调度，不改变协议或服务端负载契约。
- `假设`：runtime 启动完成时的窗口状态只建立前台基线，不额外补跑一次 WindowActivated；之后仅真实 `非前台 -> 前台` 跃迁触发。若启动期间窗口状态变化，shell 在 runtime 接管后重放的最新状态负责形成跃迁。
- `假设`：周期调度属于当前账户 runtime 生命周期；注销、切换或退出先停止并等待调度观察者，再释放 Sync coordinator、cache 和 session。

### 范围

- 必须实现：
  - 增加账户级自动 Sync 调度器：启动后周期请求 `Periodic`，窗口前台上升沿请求 `WindowActivated`，重复相同 activity 不重复触发。
  - 调度器使用注入式时间等待以确定性测试；长同步不堆积周期任务，失败不会永久杀死后续周期，所有异常日志保持脱敏。
  - runtime 在 Startup 完成后原子建立 activity 基线并启动调度；终止时先取消/等待调度，再 Dispose Sync coordinator，防止旧账户迟到请求。
  - 覆盖启动前 activity、启动后跃迁、重复事件、周期重复、在途同步合并、失败后继续、调用者/账户取消、logout/switch/dispose 顺序和日志边界。
- 允许修改：
  - `src/RelayCove.Client/Accounts/` 与对应 `tests/RelayCove.Client.Tests/Accounts/`。
  - 本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - Shared/Server 协议、SQLite schema/migration、HTTP retry、Sync 合并优先级、通知策略、DPAPI、WPF 布局或新依赖。
  - Reply、@、附件、链接/复制、日期/新消息分割线、搜索、更新器、部署和 VPS/双客户端实机 Gate。

### 验收标准

- [x] Startup 完成前不启动周期请求；初始前台只作基线，完成后每个真实前台上升沿恰好请求一次 WindowActivated，重复 Activated/StateChanged/VisibilityChanged 不重复。
- [x] 默认每 5 分钟产生 Periodic 请求；测试可无真实等待推进多个周期。一次请求未完成时不累积自动任务，完成、瞬态/永久失败或异常后仍可进入下一周期。
- [x] 自动触发复用既有 coordinator single-flight/优先级；不引入第二同步循环，不改变通知轮次原因和恢复语义。
- [x] Logout、账户切换和 Dispose 取消 delay/观察者并在 Sync coordinator 前完成调度清理；停止后没有旧 scope 请求、未观察异常或死锁。
- [x] Fast/Full、runtime/scheduler/coalescing 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；VPS/双客户端场景如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~AutomaticSync|FullyQualifiedName~ClientAccountRuntime|FullyQualifiedName~ClientSyncCoordinator"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、既有 Sync 原因优先级、通知恢复策略、认证/账户所有权或引入新依赖。
- 需要并行第二同步循环、无限重试、停止后继续旧账户网络请求，或读取真实 VPS 配置才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 4.2、9.2–9.4、12.4–12.8、21.2、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现窗口激活与周期 Sync 自动触发，不改变同步协议、通知策略、数据库、WPF 功能或服务端。
先冻结 runtime 启动/终止与调度所有权，再实现可测试的有界调度器；所有自动触发必须复用现有 ClientSyncCoordinator。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- `64f6985a48f1aaeec48af36bd64f17d87b0f8341` 新增账户级 `ClientAutomaticSyncScheduler`：Startup 完成后以当前前台状态建立基线，后续仅前台上升沿请求 `WindowActivated`；每次自动请求观察完成后等待默认 5 分钟再请求 `Periodic`，长 flight 不积累 timer tick。
- 调度器只调用既有 `IClientAccountSyncCoordinator`，不复制 HTTP、游标、重试、原因优先级或通知逻辑；同步失败/异常脱敏记录后继续下一周期，时钟异常停止以避免热循环。
- runtime factory 把调度器纳入当前账户所有权；注销、切换与退出先取消并等待自动 delay/观察者，再释放 realtime、Sync coordinator、cache 和 session。启动前 activity 可缓冲，停止后的 activity fail-closed。
- `DEC-036` 冻结 runtime 所有权、窗口上升沿、五分钟完成后节拍与终止顺序；Shared/Server、SQLite、通知策略、WPF 布局和依赖均未改变。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` / `-Mode Full` | 两轮最终门禁通过；Debug/Release 0 警告、0 错误，format、`git diff --check` 通过；Shared 35、Server 175、Client 540、Updater 1，共 751/751。 |
| `已验证` | scheduler/runtime 定向 | 新增与 runtime 定向 38/38；覆盖初始前台基线、重复 activity、多个上升沿、默认间隔、顺序周期、同步结果/异常后继续、clock 故障脱敏、在途取消与清理顺序。 |
| `已验证` | scheduler + runtime + 既有 Sync coordinator Release 重复 | 每轮 67 项，连续 10 轮共 670/670；既有 single-flight、pending 原因与优先级回归未变。 |
| `已验证` | EF model drift / NuGet 漏洞 / WPF smoke | EF 无待迁移模型变化；8 个项目无已知 vulnerable package；真实 Release WPF 主窗口非零句柄且响应，第二实例退出码 0、仅保留一个进程，精确 PID 清理后残留 0。 |
| `未验证` | 真实丢推送、五分钟壁钟、VPS/双客户端 | 本切片未读取用户提供的 VPS 配置；保留到 M5 端到端 Gate。 |
| `未验证` | Claude 独立结论 | #61 MCP 因认证源优先级失败；#62 本机后台 `0f5dda0b` / session `0f5dda0b-e23e-480d-9fa1-014b1cbdb360` 以 Opus/XHigh/plan/Read-Glob-Grep 启动，但订阅额度 403 后停在 idle/blocked，由主代理停止。两次均未读取代码或返回发现，不冒充审查通过。 |

### 文件范围

- 新增：`src/RelayCove.Client/Accounts/ClientAutomaticSyncScheduler.cs`、`tests/RelayCove.Client.Tests/Accounts/ClientAutomaticSyncSchedulerTests.cs`。
- 修改：`ClientAccountRuntime.cs`、`ClientAccountRuntimeFactory.cs`、`ClientAccountRuntimeTests.cs`、`docs/ai/DECISIONS.md` 与本任务/状态记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-036`；默认五分钟是规范未给数值时的明确客户端假设，未来若要外部配置须另行冻结校验与部署口径。
- 已知限制：真实五分钟等待、推送丢失、VPS/双客户端仍留到 M5 Gate；时钟基础设施自身抛异常时调度停止并记录，不做可能热循环的自恢复。

### 下一步

- 继续阶段 8 下一最小聊天闭环，优先盘点 Reply / @ 输入与现有服务端契约的可独立切片；真实 VPS/双客户端仍保留到 M5 Gate。
