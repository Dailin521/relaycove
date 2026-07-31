# 阶段 0：全局 Claude Second Brain

## 任务定义

- **状态：** 已完成
- **基准提交：** `1a4ecfcec17b5bc80f273dcb030eed3720a9442a`
- **工作分支：** `agent/stage-0-global-claude-second-brain`
- **相关文档：** `AGENTS.md`、`docs/ai/WORKFLOW.md`

### 目标

让本机所有 Codex 项目都能发现只读 `consult_claude`，同时确保 Claude 读取当前任务的
工作区，而不是 MCP 源码所在的 RelayCove 仓库。

### 已知事实

- `已验证`：当前 MCP 只在 RelayCove 的 `.codex/config.toml` 注册。
- `已验证`：当前桥接器从自身源码目录向上查找仓库，并在提示中硬编码 RelayCove。
- `已验证`：Codex 用户级配置位于 `~/.codex/config.toml`，新会话会加载其中的 MCP。

### 范围

- 必须实现：
  - 泛化 MCP 的工作区定位和提示
  - 安装用户级 MCP
  - 增加一条用户级调用规则
  - 在两个不同仓库验证工作区隔离
- 允许修改：
  - `tools/claude-second-brain/`
  - 本任务文件
  - `C:\Users\Administrator\.codex\config.toml`
  - `C:\Users\Administrator\.codex\AGENTS.md`
- 明确不做：
  - 不创建 Skill 或 Plugin
  - 不开放 Claude 写权限、Bash 或会话持久化
  - 不修改产品规格或业务代码

### 验收标准

- [x] 用户级 Codex 配置能发现 `consult_claude`。
- [x] RelayCove 与另一仓库的真实 smoke 均读取各自的 `AGENTS.md`。
- [x] 默认仍为 Opus XHigh，Max 只能显式请求。
- [x] 单元测试和差异检查通过。

### 验证命令

```powershell
cd tools/claude-second-brain
npm install
npm test
npm run smoke
codex mcp get claude_second_brain
git diff --check
```

### 停止并询问

- 用户级配置需要保存凭据或开放写权限。
- 当前工作区无法从 MCP 启动目录可靠解析。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 把 MCP 的提示、服务名和工作区定位改为项目无关。
- 每次调用显式绑定绝对工作区，并用 `--add-dir` 限定目标项目。
- 清除可能污染 Claude 项目判断的继承环境变量，只加载用户级 Claude 设置。
- 将打包副本安装到 `~/.codex/mcp/claude-second-brain`，并在用户级
  `config.toml` 注册。
- 在全局 `AGENTS.md` 增加 Second Brain 的窄范围触发规则。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `npm test` | 11/11 通过 |
| `已验证` | RelayCove 真实 smoke | 返回仓库独有标记 `RELAYCOVE` |
| `已验证` | `oss-maintainer-hub` 真实 smoke | 返回仓库独有标记 `OSS_MAINTAINER_HUB` |
| `已验证` | 用户级安装副本真实 smoke | 从另一仓库调用成功；Claude Opus 5 Low |
| `已验证` | Desktop Codex `mcp get` | 在另一仓库解析到用户目录中的绝对 MCP 路径 |
| `已验证` | Desktop Codex `doctor --json` | overall/config/MCP 均为 `ok` |
| `已验证` | 另一仓库的新 Codex ephemeral 会话 | 从全局 `AGENTS.md` 识别出 `claude_second_brain` 规则 |
| `已验证` | `git diff --check` | 通过 |

### 文件范围

- 新增：本任务文件、用户级 MCP 安装副本、用户级 `AGENTS.md`
- 修改：MCP 源码、测试、说明、锁文件和用户级 Codex 配置
- 删除：无

### 决策与限制

- 决策：使用用户级 MCP 加全局 `AGENTS.md`，不增加 Skill。
- 已知限制：Claude 调用保持无状态；新任务或重启后才加载新增 MCP；仓库事实仍需
  主代理本地验证。PATH 中旧版 `codex-cli 0.130.0` 不兼容当前项目配置，验证使用
  Desktop 自带 `0.146.0-alpha.3.1`。

### 下一步

- 完成规格补丁任务。
