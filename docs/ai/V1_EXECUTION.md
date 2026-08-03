# RelayCove v1 外层执行状态

> 本文件不定义产品范围或架构，也不替代任务验收。产品与架构真源依次为 [`RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md)、[`DECISIONS.md`](DECISIONS.md) 和当前活动任务；执行与证据规则以 [`WORKFLOW.md`](WORKFLOW.md) 为准。

## 当前快照

```text
ExecutionStatus: running
CurrentMilestone: M1
CurrentStage: 阶段 2
ActiveTask: docs/ai/tasks/2026-08-03-stage-2-admin-bootstrap.md
TaskStatus: running
IntegrationBranch: agent/v1-integration
LatestGreenCodeCommit: b72194a168ba99a5268661df8f8cac7c48578fc4
LatestGreenIntegrationCommit: 80bb74270e5b15a47fb4bbc7ae19deacd47f22ec
NextAction: 固定 admin bootstrap ChallengeHead，反证凭据、密码策略、动态授权与并发边界
ClaudeCalls: 18（软上限 24，硬上限 30）
ClaudeCostUsd: 5.4229485 confirmed；另有十一次失败/中断调用费用 unavailable
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
| M0 | `completed` | 同步契约、`DEC-003`、解决方案和真实 Fast/Full 验证均通过 | 已进入 M1 |
| M1 | `running` | 可构建骨架、认证共享契约/存储（`DEC-004/005`）与认证 HTTP/轮换闭环（`DEC-006`）已完成 | 管理员用户生命周期、权限、文字消息、历史与 SignalR 形成纵向闭环 |
| M2 | `pending` | 尚未开始 | Internal Alpha 验收证据完整 |
| M3 | `pending` | 尚未开始 | Beta 验收证据完整 |
| M4 | `pending` | 尚未开始 | RC 自动化、包与发布材料完整 |
| M5 | `pending` | 尚未开始 | 自动验证完成；真实 Windows/VPS/双客户端 Gate 明确记录 |

里程碑顺序来自当前 v1 执行目标；每个里程碑的功能口径和最终交付标准仍由工程方案、决策记录和对应最小纵向任务冻结，本文件不预写实现细节。

## 集成与绿色状态

- `agent/v1-integration` 是本地最新绿色集成头；任务分支只有完成验证和交接提交后，才允许仅快进该分支。
- 当前认证端点代码检查点 `b72194a168ba99a5268661df8f8cac7c48578fc4` 已通过 Fast、Full、73 项测试、真实 SQLite 并发/锁库、JWT 负向、限流、日志与依赖漏洞审计；候选 Claude MCP 未取得结论，已如实降级记录 Codex 固定差异自审。
- `LatestGreenCodeCommit` 只记录已经通过任务要求的真实源代码提交；后续若验证失败，不得推进该值或集成分支。
- 用户已明确授权绿色任务的常规 push、合入集成分支与任务分支清理，无需二次确认；`main`、Tag、Release、真实发布和生产部署仍须满足对应里程碑与发布 Gate，不由该授权自动放宽。

## Claude 使用账本

| # | 日期 | 任务 | 类型 | 请求模型/档位 | 结果 | `cost_usd` |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 2026-08-03 | 同步契约 | 前置 challenge | Opus / XHigh | CLI 认证源冲突，未返回 `workspace_root`、实际模型或 `model_mismatch`；调用前后仓库状态一致 | `unavailable` |
| 2 | 2026-08-03 | 同步契约 | 候选 review | Opus / XHigh | 同一认证错误；固定 `ReviewHead` 未变化，降级 Codex 复核通过 | `unavailable` |
| 3 | 2026-08-03 | 认证共享契约 | 前置 challenge | Opus / XHigh | 同一认证错误；`ChallengeHead=6d60f9ae22a392adb75970763f260b10e53ebdbc` 与干净状态未变化，降级 Codex 反证 | `unavailable` |
| 4 | 2026-08-03 | 认证共享契约 | 候选 review | Opus / XHigh | 会话在返回前中断，无结果元数据；固定 `ReviewHead=9a867323095c9753c96cf55985396229d9088059` 未变化 | `unavailable` |
| 5 | 2026-08-03 | 认证共享契约 | 候选 review 重试 | Opus / XHigh | `claude_second_brain` MCP 仍因旧认证环境失败；未返回模型、workspace、mismatch 或费用 | `unavailable` |
| 6 | 2026-08-03 | 认证共享契约 | 只读 CLI 回退 | Opus / XHigh | 实际 `claude-opus-5`；在形成结论前触及预算，`terminal_reason=budget_exhausted` | `$0.5187985` |
| 7 | 2026-08-03 | 认证共享契约 | 只读 CLI 候选 review | Opus / XHigh | `workspace=E:\WorkSpace\RelayCove`（CLI 限域）、实际 `claude-opus-5`、mismatch=`false`、固定 ReviewHead 不变；`FIX_REQUIRED`，发现已修正 | `$0.666895` |
| 8 | 2026-08-03 | 认证共享契约 | 只读 CLI 定向复审 | Opus / XHigh | `ReviewHead=836d1e223d2cd9026fdf935be9cb16affbf45cf8`、实际 `claude-opus-5`、mismatch=`false`；五项原发现关闭，`PASS`；新增非阻塞 P3 已本地修正验证 | `$0.14818675` |
| 9 | 2026-08-03 | 认证存储 | 前置 challenge | Opus / XHigh | `claude_second_brain` 仓库只读调用在 300 秒工具上限被截断，无结构化结果 | `unavailable` |
| 10 | 2026-08-03 | 认证存储 | 只读 CLI challenge | Opus / XHigh | 仓库限域 CLI 在 300 秒外层上限被截断，无终局结果 | `unavailable` |
| 11 | 2026-08-03 | 认证存储 | 无仓库 MCP challenge | Opus / XHigh | 已提供仓库事实但仍在 300 秒 MCP 上限被截断，无结构化结果 | `unavailable` |
| 12 | 2026-08-03 | 认证存储 | 无工具 CLI challenge | Opus / XHigh | 实际 `claude-opus-5`、mismatch=`false`、提供事实对应 `ChallengeHead=6b821f1e9ba23b005630a3781fd407737e579684`；`REVISE`，有效发现纳入 `DEC-005` | `$0.3419475` |
| 13 | 2026-08-03 | 认证存储 | 只读 CLI 候选 review | Opus / XHigh | `ReviewHead=134fea6ceca3ec40aa8e5ce7e35a66eb1ba83d9a`；600 秒外层上限前无终局结果，本地降级复核发现并修复时间精度、CHECK 与用户名不变量缺口 | `unavailable` |
| 14 | 2026-08-03 | 认证存储 | 最终 MCP review | Opus / XHigh | `claude_second_brain` 接单后因 wrapper 认证源优先级导致 claude.ai connector 被禁用，未返回结构化模型、workspace 或费用 | `unavailable` |
| 15 | 2026-08-03 | 认证存储 | safe-mode 只读 CLI 最终 review | Opus / XHigh | 显式工作目录 `E:\WorkSpace\RelayCove`，工具限于 `Read/Glob/Grep`；实际 `claude-opus-5`、请求模型无偏差，`Base=0e5eefb..ReviewHead=6b0c85e`；耗时 `782578 ms`，`PASS`，无阻塞发现 | `$2.35148075` |
| 16 | 2026-08-03 | 认证端点 | safe-mode 只读 CLI challenge | Opus / XHigh | `ChallengeHead=87ff08aa5c7258a0b48cd37733a9b6fcd1d0b8d9`；实际 `claude-opus-5`，返回 `REVISE`；确认毫秒时钟和并发轮换阻塞项，中段本地输出被截断 | `$0.82506775` |
| 17 | 2026-08-03 | 认证端点 | safe-mode 只读 CLI 定向 challenge | Opus / XHigh | 同一 ChallengeHead，实际 `claude-opus-5`；完整返回 7 项 `REVISE` 修正，经本地代码与 Microsoft.Data.Sqlite 事务证据核对后纳入 `DEC-006` | `$0.57057225` |
| 18 | 2026-08-03 | 认证端点 | 最终 MCP review | Opus / XHigh | `ReviewHead=b72194a`；本机 `ANTHROPIC_API_KEY`/其他认证源优先于 claude.ai 登录，connector 被禁用，未返回模型、workspace、费用或审查结论；按用户要求未重复耗时调用，降级 Codex 固定差异自审 | `unavailable` |

- 调用计数：`18 / 24 soft / 30 hard`。
- 已确认费用合计：`$5.4229485`；其余十一次未返回费用，保持 `unavailable`，不得推定为 `$0`。
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
