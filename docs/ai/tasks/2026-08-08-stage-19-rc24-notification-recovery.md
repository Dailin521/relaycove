# 阶段 19：rc.24 通知、重连与恢复门禁

## 任务定义

- **任务名称：** 阶段 19 rc.24 通知、重连与恢复门禁
- **状态：** `已完成`
- **基准提交：** `b8a7fb1fc7537b74a58673913d5d5a5292e7b5ce`
- **工作分支：** `agent/stage-19-rc24-stabilization`
- **相关方案章节：** 阶段 6、阶段 7、阶段 12、21.1、21.5、21.6
- **详细执行计划：** [`../RC24_EXECUTION_PLAN.md`](../RC24_EXECUTION_PLAN.md)

### 目标

以 rc.23 为绿色基线，验证通知、托盘、断网重连、消息补拉和更新恢复的自动化边界，找出真实失败或覆盖缺口。首个修复只处理由验证证据确认的问题，不预造新功能或扩大协议范围。

### 已知事实

- `已验证`：`main` 基准提交为 `b8a7fb1`，状态页记录 Full/Release 0 警告、0 错误及 1,637 项测试通过。
- `已验证`：工程方案要求自动化覆盖 Startup、WindowActivated、Reconnect/Periodic、阈值 `10/11`、Toast 临时失败、同步失败时 Realtime 候选解闸和串行协调边界。
- `已验证`：真实 Windows 通知、托盘、断网恢复及 rc.23→rc.24 更新仍需要安装态或发布包人工验收，自动化不能代替。
- `已验证`：当前没有 owner 提供的 rc.23 新缺陷清单，`docs/ai/STATUS.md` 也没有活动任务或阻塞项。

### 假设

- `假设`：若现有自动化全部通过且没有高风险覆盖缺口，本切片只记录绿色门禁，不修改产品代码；下一切片等待真实使用反馈或执行 rc.23→rc.24 发布包更新演练。

### 范围

- 必须实现：
  - 运行 Fast 基线和 Client 通知、Realtime、Sync、Desktop、Updates 定向回归。
  - 对照工程方案 21.1、21.5、21.6 核对自动化覆盖，记录已覆盖和必须实机验证的场景。
  - 对任何可重复失败先建立最小回归，再实施最小修复并重复验证。
  - 保持账户隔离、撤权 fail-closed、通知去重和消息可靠性语义不变。
- 允许修改：
  - `src/RelayCove.Client/`
  - `tests/RelayCove.Client.Tests/`
  - `docs/ai/tasks/2026-08-08-stage-19-rc24-notification-recovery.md`
  - 状态真实变化时修改 `docs/ai/STATUS.md`
- 明确不做：
  - 不修改 Shared/Server 协议、数据库或 SignalR 事件。
  - 不实现逐成员频道角色，不新增 UI 框架、基础设施或大型依赖。
  - 不把未执行的 Windows 安装态场景标记为通过。
  - 不推送、合并、部署或修改内部更新通道。

### 验收标准

- [ ] Fast 基线通过，结果记录精确测试数量和构建状态。
- [ ] 通知、Realtime、Sync、Desktop、Updates 定向测试通过；失败时有回归与最小修复。
- [ ] 21.1、21.5、21.6 的自动化覆盖与实机边界有清晰证据矩阵。
- [ ] 产品代码若发生变化，Full 与 Release 0 警告、0 错误且全部测试通过。
- [ ] 未执行的 rc.23→rc.24 安装态更新、双账号和断网人工场景明确标记为 `未验证`。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test ./tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Notifications|FullyQualifiedName~Realtime|FullyQualifiedName~Sync|FullyQualifiedName~Desktop|FullyQualifiedName~Updates"
pwsh ./scripts/verify.ps1 -Mode Full
```

### 停止并询问

- 可重复失败需要改变公共协议、数据库、授权或消息可靠性语义时停止并拆分独立任务。
- 真实 Windows 验收需要用户凭据、第二账户、系统设置变更或内部更新通道写入时停止并取得明确授权。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、相关工程方案章节、docs/ai/STATUS.md 和本任务文件。
只实现“范围”中的内容，不处理“明确不做”的事项。
先确认仓库事实和基线，再完成最小可验收实现。
运行列出的验证；未运行的项目必须标注“未验证”并说明原因。
触发停止条件时保留现场并询问，不自行扩大权限或范围。
完成后更新本文件的结果部分；验证通过后允许本地提交，但不得推送或合并。
```

## 任务结果

### 修改摘要

- 已完成 rc.24 的 S0/S1 自动化门禁：Fast、通知/Realtime/Sync/Desktop/Updates 定向回归、WPF 内置三档快照和 Full 均通过。
- 未修改产品代码。为保持仓库锁定的 `global.json` 不变，在用户本地 RelayCove 工具目录安装 .NET SDK `10.0.101`，并将本仓库 Git `core.autocrlf` 覆盖为 `false` 以符合 `.editorconfig` 的 LF 规则。
- 内置快照独立审阅发现窄窗口成员抽屉可重新打开并覆盖发送区的 P2；已拆分为独立任务 `2026-08-08-stage-19-narrow-member-drawer.md`。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | Fast 基线 | .NET SDK `10.0.101`；Debug 0 警告、0 错误；Shared 70、Server 353、Client 1,176、Updater 38，共 1,637 项通过 |
| `已验证` | Client 可靠性定向回归 | Release 0 警告、0 错误；`Notifications|Realtime|Sync|Desktop|Updates` 过滤器下 827/827 通过 |
| `已验证` | WPF 内置快照 | `ClientUiSnapshotTests` 4/4 通过；生成 1280×720、1600×900、1920×1080 三份 PNG，并由内部图片查看工具审阅 |
| `已验证` | Full 与 Release | format、Release 构建、Shared 70、Server 353、Client 1,176、Updater 38，共 1,637 项，以及 `git diff --check` 通过；0 警告、0 错误 |
| `未验证` | Windows 安装态通知、断网、双账号与 rc.23→rc.24 更新 | 需要后续发布包和真实环境 |

### 自动化覆盖矩阵

| 场景 | 已验证测试证据 | 实机边界 |
| --- | --- | --- |
| Startup、WindowActivated、Reconnect | `ClientNotificationRoundCoordinatorTests` 的 startup 汇总、WindowActivated 历史抑制与 Reconnect 前后台策略 | 真实双账户提示与系统通知中心 |
| 10/11 阈值、临时 Toast 失败与恢复 | `ClientNotificationCoordinatorTests.DispatchAutomatic_WhenCandidateCountCrossesBoundary_SelectsExpectedPolicy`、`WindowsClientNotificationPlatformTests`、`NotificationRecoveryTests` | 原生 Toast/托盘回退与系统设置 |
| Sync 失败时 Realtime 解闸与串行边界 | `ClientNotificationRoundCoordinatorTests.FailedBackgroundRound_DispatchesRealtimeAndOldRecoveryButKeepsSyncCandidate`、`RealtimeCloseRace_DispatchesEveryCandidateExactlyOnce`、`ConcurrentDispatches_AreSerializedPerAccount` | 真实网络中断和恢复 |
| SignalR 重连、权限洞、游标与账户隔离 | `ClientRealtimeConnectionTests.Connection_WhenEstablishedTransportDrops_ReportsReconnectingThenConnected`、`ClientSyncCoordinatorTests`、`SyncPageCommitTests` | 双账户、私有撤权与旧通知点击 |
| 更新清单、SHA-256、强制门禁与恢复 | `ClientUpdateCoreTests`、`ClientUpdateHandoffTests`、Updater recovery tests | 精确 rc.23→rc.24 交接和失败保护 |

### 文件范围

- 新增：`docs/ai/tasks/2026-08-08-stage-19-rc24-notification-recovery.md`
- 修改：`docs/ai/STATUS.md`、`docs/ai/RC24_EXECUTION_PLAN.md`
- 删除：无

### 决策与限制

- 决策：先以自动化和内置快照证据选择修复，不在没有复现或反馈时改动可靠性核心。
- 已知限制：当前切片不能用自动化替代 Windows 安装态通知、托盘、断网、双账号、旧 Toast 点击和 rc.23→rc.24 更新人工验收。

### 下一步

- 执行独立 P2：修复窄窗口重新打开成员抽屉时覆盖聊天输入区的问题，并把 1280 WPF 快照拆为可达的附件和提及状态。
