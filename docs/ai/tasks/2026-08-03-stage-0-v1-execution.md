# 阶段 0：建立 v1 外层执行状态

## 任务定义

- **任务名称：** 阶段 0 — 建立 `V1_EXECUTION.md`
- **状态：** 进行中
- **基准提交：** `a458f7c40589b50d26518f7c43465a2088785203`
- **工作分支：** `agent/stage-0-v1-execution`
- **相关方案章节：** `docs/ai/WORKFLOW.md`、`docs/ai/STATUS.md`；产品和架构仍以 `RelayCove_工程落地方案.md` 与 `docs/ai/DECISIONS.md` 为准

### 目标

新增一个可在会话中断后恢复的 v1 外层执行账本，记录里程碑、活动任务、绿色集成头、下一动作、Claude 使用、阻塞和用户 Gate，但不重新定义产品、架构或各切片实现细节。

### 已知事实

- `已验证`：同步契约任务已完成，最终提交为 `a458f7c40589b50d26518f7c43465a2088785203`。
- `已验证`：本地 `agent/v1-integration` 已创建并指向上述绿色文档基线。
- `已验证`：仓库仍没有 `RelayCove.sln`、业务代码和 `scripts/verify.ps1`。
- `已验证`：同步契约任务发起 2 次 Claude 调用，均在 CLI 连接器初始化阶段失败，未返回模型、`workspace_root`、`model_mismatch` 或费用。

### 假设

- `假设`：后续每个任务完成后，在同一任务的交接提交中更新 `V1_EXECUTION.md`，避免它与 `STATUS.md` 和任务记录漂移。

### 范围

- 必须实现：
  - 新增 `docs/ai/V1_EXECUTION.md`，包含当前里程碑/阶段、活动任务、任务状态、集成分支、最近绿色代码提交、下一明确动作、Claude 调用与费用、阻塞、用户 Gate 和顶层状态。
  - 记录 `running / blocked / v1_rc_ready / released` 状态枚举及当前值，但不自行宣布 RC 或发布。
  - 同步 `docs/ai/STATUS.md` 的活动任务和下一动作。
- 允许修改：
  - `docs/ai/V1_EXECUTION.md`
  - `docs/ai/STATUS.md`
  - 本任务文件
- 明确不做：
  - 不创建解决方案、业务代码、测试、验证脚本或其他阶段任务。
  - 不修改工程方案、架构决策或已完成同步契约任务。
  - 不推送、合并、发布或部署。

### 验收标准

- [ ] `V1_EXECUTION.md` 明确声明自己不是产品或架构真源，并链接权威文件。
- [ ] 所有要求字段有可机器搜索的稳定标签和真实当前值。
- [ ] 当前顶层状态为 `running`，M0 为进行中，其余里程碑为待开始。
- [ ] 最近绿色代码提交明确为“无”，不能把纯文档提交伪装成绿色代码；另记绿色集成基线。
- [ ] Claude 次数为 2，费用明确标成工具未返回而非猜测为 0。
- [ ] 最终差异只包含允许的 3 个文件，且 `git diff --check` 通过。

### 验证命令

```powershell
$path = 'docs/ai/V1_EXECUTION.md'
$required = @(
    'ExecutionStatus: running',
    'CurrentMilestone: M0',
    'CurrentStage: 阶段 0',
    'ActiveTask:',
    'TaskStatus:',
    'IntegrationBranch: agent/v1-integration',
    'LatestGreenCodeCommit:',
    'LatestGreenIntegrationCommit:',
    'NextAction:',
    'ClaudeCalls:',
    'ClaudeCostUsd:',
    'Blocker:',
    'RequiredUserGate:'
)
foreach ($marker in $required) {
    & rg -q -F -- $marker $path
    if ($LASTEXITCODE -ne 0) { throw "缺少执行标记：$marker" }
}

& rg -q -F -- '本文件不定义产品范围或架构' $path
if ($LASTEXITCODE -ne 0) { throw '缺少真源边界声明' }

$allowed = @(
    'docs/ai/V1_EXECUTION.md',
    'docs/ai/STATUS.md',
    'docs/ai/tasks/2026-08-03-stage-0-v1-execution.md'
)
$changed = @(git -c core.quotepath=false diff --name-only 'a458f7c40589b50d26518f7c43465a2088785203..HEAD')
$unexpected = @($changed | Where-Object { $_ -notin $allowed })
if ($unexpected.Count -gt 0) { throw "范围外文件：$($unexpected -join ', ')" }
$missing = @($allowed | Where-Object { $_ -notin $changed })
if ($missing.Count -gt 0) { throw "缺少文件：$($missing -join ', ')" }
git diff --check 'a458f7c40589b50d26518f7c43465a2088785203..HEAD'
if ($LASTEXITCODE -ne 0) { throw '空白检查失败' }
```

### 停止并询问

- 账本需要重定义产品范围、里程碑顺序或架构边界。
- 发现同步契约完成提交不再是当前分支祖先，或集成分支不能仅快进。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只创建 v1 外层执行账本，不复制或改写产品与架构。
所有状态和 SHA 从当前 Git 与仓库事实取得；未知费用必须标为未返回。
运行稳定标记、文件白名单和空白检查，完成后更新任务结果并本地提交。
不得创建业务代码，不得推送或合并。
```

## 任务结果

### 修改摘要

- 待实现后填写。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | 稳定字段检查 | 待运行 |
| `未验证` | 文件白名单与 `git diff --check` | 待运行 |

### 文件范围

- 新增：待填写。
- 修改：待填写。
- 删除：无。

### 决策与限制

- 决策：`V1_EXECUTION.md` 只负责外层执行状态，不重新定义产品或架构。
- 已知限制：Claude 两次失败调用没有返回费用，不能把未知值记录为已验证的 `$0`。

### 下一步

- 创建可构建解决方案和真实 `Fast` / `Full` 验证脚本任务。
