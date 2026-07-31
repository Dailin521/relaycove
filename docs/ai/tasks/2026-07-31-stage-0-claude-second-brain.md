# 阶段 0：Claude Second Brain MCP

## 任务定义

- **状态：** 已完成
- **基准提交：** `2daeb78f489ec062cae3bc176aa04e1d60ae04a2`
- **工作分支：** `agent/stage-0-claude-second-brain`
- **相关文档：** `AGENTS.md`、`docs/ai/WORKFLOW.md`

### 目标

将本机 Claude Code CLI 封装为项目级只读 MCP，并为 Codex 配置快速探索和严格审查两类子代理模型。主代理继续继承全局 `gpt-5.6-sol + xhigh`。

### 已知事实

- `已验证`：Claude Code `2.1.220` 已登录并能完成 JSON 无交互调用。
- `已验证`：Claude 原生后台会话返回 `CLAUDE_BACKGROUND_OK`。
- `已验证`：本机 Node.js `v24.11.1`。
- `已验证`：Codex 官方手册支持项目级 MCP、默认子代理模型和自定义 agent 文件。
- `未验证`：Gmail 网页链接无法解析为 Gmail API message/thread ID，邮件内容未纳入本任务决策。

### 范围

- 必须实现：
  - 一个只读 `consult_claude` stdio MCP 工具
  - 参数校验、超时、预算限制、结构化结果与错误处理
  - 单元测试和一次真实 MCP 端到端验证
  - 项目级 Claude MCP、Explorer 和 Reviewer 子代理配置
- 允许修改：
  - `.gitignore`
  - `.codex/`
  - `tools/claude-second-brain/`
  - `docs/ai/`
- 明确不做：
  - 不修改全局 Codex 或 Claude 配置
  - 不向 Claude 暴露 Bash、编辑或写入工具
  - 不推送、不合并

### 验收标准

- [x] Claude 后台探针成功。
- [x] MCP 能列出并调用 `consult_claude`。
- [x] Claude 仓库工具严格限制为 `Read,Glob,Grep`。
- [x] 单元测试覆盖参数、只读边界、JSON 解析和错误。
- [x] 默认子代理为 Terra High，Reviewer 为 Sol High。
- [x] 项目配置不覆盖主代理模型与 reasoning effort。
- [x] 工作区验证通过并创建本地提交。

### 停止并询问

- 需要把 Claude 凭据写入项目。
- 需要修改全局 Codex 配置或开放 Claude 写权限。

## 任务结果

### 修改摘要

- 新增一个 stdio MCP，以无交互方式调用本机 Claude Code CLI。
- MCP 只向 Claude 开放 `Read`、`Glob` 和 `Grep`，并限制模型、effort、预算、超时和输出大小。
- 结果返回请求模型、实际模型、路由差异、费用、耗时和有限告警。
- 新增 Terra High Explorer、Sol High Reviewer 和默认 Terra High 子代理路由。
- 增加纯函数单元测试、真实 MCP smoke test、使用说明和依赖锁文件。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | Claude 原生后台探针 | `CLAUDE_BACKGROUND_OK` |
| `已验证` | `npm install` | 安装 93 个包；审计 94 个包，0 个漏洞 |
| `已验证` | `npm test` | 7/7 通过；覆盖只读参数、默认值、JSON 解析和失败载荷 |
| `已验证` | `npm run smoke` | MCP 读取 `AGENTS.md` 并返回 `Repository Guidelines`；实际模型 `claude-opus-5` |
| `已验证` | Desktop Codex `mcp list/get` | `claude_second_brain` enabled；工具白名单与超时配置正确 |
| `已验证` | Desktop Codex `doctor --json` | overall/config/MCP 均为 `ok`；主模型仍为 `gpt-5.6-sol` |
| `已验证` | 请求 `sonnet` 的路由探针 | 实际模型为 `claude-opus-5`，桥接器返回 `model_mismatch=true` |
| `已验证` | Claude 独立审查 | 约 140 秒完成；修复 JSON、环境默认值、错误分类和路由透明度问题 |
| `未验证` | `dotnet build` / `dotnet test` | 仓库尚无解决方案或业务代码 |

### 决策与限制

- 决策：Claude MCP 默认 Opus High；普通 Codex 子代理默认 Terra High；独立 Reviewer 使用 Sol High。
- 决策：Claude 调用超时为 240 秒，Codex MCP 工具总超时为 300 秒；达到超时必须返回失败。
- 限制：当前任务加载不到新 MCP，需新建任务或重启 Codex 后使用。
- 限制：PATH 中旧版 `codex-cli 0.130.0` 不支持新版配置；已使用 Desktop 自带 `0.146.0-alpha.3.1` 验证。
- 限制：当前 Claude 环境把 `sonnet` 请求路由到 Opus；调用者必须检查 `model_mismatch`。
- 限制：深度审查可能接近超时，应限制文件范围或按子系统拆分。
- 限制：Gmail 网页 token 无法作为 API message/thread ID 解析，邮件内容未参与模型选择。

### 下一步

- 新建 Codex 任务或重启客户端，确认 `consult_claude` 在正式工具列表中可调用。
