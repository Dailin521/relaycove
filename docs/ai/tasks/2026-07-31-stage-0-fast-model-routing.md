# 阶段 0：Fast 与模型路由

## 任务定义

- **状态：** 已完成
- **基准提交：** `514f12c6360628e57d0dcf4e420ad6805c362616`
- **工作分支：** `agent/stage-0-fast-model-routing`
- **相关方案章节：** `RelayCove_工程落地方案.md` 阶段 0

### 目标

让本机及 RelayCove 项目的 Codex 默认使用 Fast，并把 Claude Second Brain
默认 effort 提升到 `xhigh`。`max` 仅作为范围明确的最终复核选项。

### 已知事实

- `已验证`：全局 Codex 当前使用 `service_tier = "priority"`，尚未启用
  `features.fast_mode`。
- `已验证`：官方 Codex 手册要求同时配置 `service_tier = "fast"` 和
  `features.fast_mode = true` 以持久启用 Fast。
- `已验证`：本机 Claude CLI 支持 `low`、`medium`、`high`、`xhigh`、`max`。
- `已验证`：当前 MCP 默认 Claude effort 为 `high`，并且枚举只开放到 `xhigh`。

### 范围

- 必须实现：
  - 全局和项目 Codex 配置显式启用 Fast
  - Claude 默认 effort 改为 `xhigh`
  - MCP 接受显式 `max`，但不将其作为默认值
  - 更新单元测试、使用说明和模型路由文档
- 允许修改：
  - `C:\Users\Administrator\.codex\config.toml`
  - `.codex/`
  - `tools/claude-second-brain/`
  - `docs/ai/`
- 明确不做：
  - 不改变主代理、Explorer 或 Reviewer 的模型与 reasoning effort
  - 不扩大 Claude 仓库权限
  - 不推送、不合并

### 验收标准

- [x] 全局与项目配置均为 `service_tier = "fast"` 且启用 `fast_mode`。
- [x] Claude 默认 effort 为 `xhigh`。
- [x] `max` 能通过参数校验并传递到 Claude CLI。
- [x] 单元测试、MCP smoke、Codex 配置检查和差异检查通过。

### 验证命令

```powershell
cd tools/claude-second-brain
npm test
npm run smoke
codex mcp get claude_second_brain
git diff --check
```

### 停止并询问

- Fast 或 `max` 需要修改模型、权限、认证信息或提高费用上限。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 全局、项目、Explorer 和 Reviewer 均显式使用 Fast；全局与项目启用
  `features.fast_mode`。
- Claude Second Brain 默认 effort 改为 `xhigh`，并开放按次 `max`。
- MCP 结构化结果新增 `requested_effort`，便于核对实际请求档位。
- smoke test 支持用环境变量验证不同 effort、预算和超时。
- 工作流和状态页记录新的速度与模型路由策略。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `npm test` | 8/8 通过 |
| `已验证` | `npm audit --omit=dev` | 0 个漏洞 |
| `已验证` | XHigh MCP smoke | Claude Opus 5；约 10.6 秒；`$0.04356775` |
| `已验证` | Max MCP smoke | Claude Opus 5；约 14.9 秒；`$0.024694` |
| `已验证` | Desktop Codex `doctor --json` | 全局与项目配置均为 `ok` |
| `已验证` | Desktop Codex `features list` | `fast_mode` 为 `true` |
| `已验证` | Desktop Codex `mcp get claude_second_brain` | MCP 启用且配置一致 |
| `已验证` | `git diff --check` | 通过 |
| `未验证` | `dotnet build` / `dotnet test` | 仓库尚无解决方案或业务代码 |

### 文件范围

- 新增：本任务文件
- 修改：全局 Codex 配置、`.codex/`、`docs/ai/`、`tools/claude-second-brain/`
- 删除：无

### 决策与限制

- 决策：Fast 是 Codex 的全局和项目默认；Claude `max` 只按次显式使用。
- 决策：主代理保持 Sol XHigh，默认子代理保持 Terra High，Reviewer 保持 Sol High。
- 已知限制：Fast 提高速度但消耗更多额度；Claude `max` 仍受预算和超时限制。
- 已知限制：PATH 中的旧 Codex CLI `0.130.0-alpha.5` 不兼容新版 agent
  配置；验证使用 Desktop 随附的 `0.146.0-alpha.3.1`。
- 已知限制：已运行的 Codex 任务可能保留启动时配置；新任务或重启会加载新默认值。

### 下一步

- 开始阶段 0 的解决方案脚手架。
