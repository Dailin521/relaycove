# RelayCove v1 外层执行状态

> 本文件不定义产品范围或架构，也不替代任务验收。产品与架构真源依次为 [`RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md)、[`DECISIONS.md`](DECISIONS.md) 和当前活动任务；执行与证据规则以 [`WORKFLOW.md`](WORKFLOW.md) 为准。

## 当前快照

```text
ExecutionStatus: running
CurrentMilestone: M0
CurrentStage: 阶段 0
ActiveTask: docs/ai/tasks/2026-08-03-stage-0-buildable-scaffold.md
TaskStatus: running
IntegrationBranch: agent/v1-integration
LatestGreenCodeCommit: none（尚未创建业务代码）
LatestGreenIntegrationCommit: 1dd6eb08c1f839bf651a433e9dc647347ef68469
NextAction: 创建并验证 .NET 10 可构建解决方案、四个源项目、四个测试项目和 Fast/Full 脚本
ClaudeCalls: 2（软上限 24，硬上限 30）
ClaudeCostUsd: unavailable（两次失败调用均未返回 cost_usd，不能推定为 0）
Blocker: none
RequiredUserGate: none
```

`ExecutionStatus` 只允许以下值：

- `running`：仍在向 v1 RC 推进。
- `blocked`：同一真实阻塞满足工作流规定的连续审计条件，且无法继续做有意义的工作。
- `v1_rc_ready`：全部 `V1_RC_READY` 条件已有当前证据证明。
- `released`：真实环境验收通过，且用户已经明确授权合并、Tag、Release 与生产部署。

## 里程碑状态

| 里程碑 | 状态 | 当前证据 | 下一出口 |
| --- | --- | --- | --- |
| M0 | `running` | 同步契约与 `DEC-003` 已完成；解决方案尚不存在 | 可构建骨架的 Fast/Full 验证通过 |
| M1 | `pending` | 尚未开始 | 认证、会话、权限、文字消息、历史与 SignalR 形成纵向闭环 |
| M2 | `pending` | 尚未开始 | Internal Alpha 验收证据完整 |
| M3 | `pending` | 尚未开始 | Beta 验收证据完整 |
| M4 | `pending` | 尚未开始 | RC 自动化、包与发布材料完整 |
| M5 | `pending` | 尚未开始 | 自动验证完成；真实 Windows/VPS/双客户端 Gate 明确记录 |

里程碑顺序来自当前 v1 执行目标；每个里程碑的功能口径和最终交付标准仍由工程方案、决策记录和对应最小纵向任务冻结，本文件不预写实现细节。

## 集成与绿色状态

- `agent/v1-integration` 是本地最新绿色集成头；任务分支只有完成验证和交接提交后，才允许仅快进该分支。
- 当前绿色集成提交 `1dd6eb08c1f839bf651a433e9dc647347ef68469` 只包含文档契约与执行账本，不是绿色代码提交。
- `LatestGreenCodeCommit` 保持 `none`，直到真实源代码、构建和对应自动化测试同时存在并通过。
- 未经用户明确授权，不 push、不合并 `main`、不创建 PR/Tag/Release、不部署，也不删除远端分支。

## Claude 使用账本

| # | 日期 | 任务 | 类型 | 请求模型/档位 | 结果 | `cost_usd` |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 2026-08-03 | 同步契约 | 前置 challenge | Opus / XHigh | CLI 认证源冲突，未返回 `workspace_root`、实际模型或 `model_mismatch`；调用前后仓库状态一致 | `unavailable` |
| 2 | 2026-08-03 | 同步契约 | 候选 review | Opus / XHigh | 同一认证错误；固定 `ReviewHead` 未变化，降级 Codex 复核通过 | `unavailable` |

- 调用计数：`2 / 24 soft / 30 hard`。
- 已确认费用合计：工具没有返回任何可核对的费用值，因此记为 `unavailable`，不得伪造 `$0`。
- Claude 恢复可用后，每次调用必须记录返回的 `workspace_root`、实际模型、`model_mismatch` 与 `cost_usd`；达到调用或费用硬上限时降级为 Codex 独立复核，不停止开发。

## 阻塞与用户 Gate

- 当前阻塞：无。
- 当前所需用户 Gate：无。
- 只有 `AGENTS.md`、`WORKFLOW.md` 和 v1 执行目标列出的重大产品、不可逆、安全、凭据、真实体验或发布事项才请求用户裁决；普通工程实现由当前任务自行收敛。

## 恢复与更新规则

会话中断后按以下顺序恢复，不要求用户复述：

1. 读取本文件的当前快照。
2. 读取 [`STATUS.md`](STATUS.md) 和 `ActiveTask`。
3. 核对 `git status --short --branch`、`agent/v1-integration`、当前任务分支和最近提交。
4. 从最后一个已验证检查点继续；未知或未运行项目保持 `未验证`。

每个任务开始时更新 `ActiveTask`、`TaskStatus`、`NextAction` 和当前分支事实；每个绿色完成提交后更新集成头、绿色提交、Claude 账本、阻塞和 Gate。只有证据实际满足时才能把状态改成 `v1_rc_ready` 或 `released`。
