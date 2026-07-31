# Claude Second Brain MCP

一个项目无关的只读 stdio MCP，将本机 Claude Code CLI 作为 Codex 的独立“第二大脑”。
它既可由仓库内 `.codex/config.toml` 注册，也可安装到用户级 Codex 配置供所有项目使用。

## 能力与边界

MCP 只暴露 `consult_claude`：

- 支持 `analysis`、`review`、`challenge`、`brainstorm` 四种视角。
- 默认使用 Claude `opus`、`xhigh` effort。
- 架构、安全、数据库和协议审查使用默认 `xhigh`；只有争议较大且范围明确的最终复核才显式使用 `max`。
- 仓库访问开启时，Claude 只能使用 `Read`、`Glob`、`Grep`。
- 每次调用显式绑定目标工作区，并只加载用户级 Claude 设置，避免跨项目读取错误仓库。
- 不开放 Bash、编辑、写文件、Chrome 或会话持久化。
- 默认单次预算上限 `$0.50`，Claude 调用超时 240 秒；Codex MCP 工具总超时 300 秒。两者都可通过项目配置调整。
- 结果同时返回请求模型、请求 effort 和 Claude CLI 报告的实际模型；模型不一致时设置 `model_mismatch=true`，不得假定别名一定被上游采用。
- 结构化结果包含实际 `workspace_root`，调用者应在跨项目审查时核对它。

Claude 的回答是第二意见，不能替代本地构建、测试、人工验收或主代理的证据检查。

## 前置条件

```powershell
claude --version
claude auth status
node --version
```

Claude 必须能够使用 `--print --output-format json` 完成无交互调用。认证信息由本机 Claude CLI 管理，不得写入仓库。

## 安装与验证

```powershell
cd tools/claude-second-brain
npm install
npm test
npm run smoke
```

`npm test` 不调用外部模型；`npm run smoke` 会通过 MCP 完成一次真实 Claude 调用，可能产生模型费用。

RelayCove 的项目级配置位于 `.codex/config.toml`。跨项目使用时，把 MCP 安装到用户目录并在
`~/.codex/config.toml` 注册；Codex 通常需要新建任务或重启客户端后才能加载新 MCP。

桥接器从 MCP 进程的启动目录定位当前项目，不依赖 RelayCove 的安装路径。用户级配置不要
设置固定 `cwd`，否则 Claude 会在所有任务中读取同一个仓库。

## 使用建议

适合调用：

- 重要架构取舍的反方论证
- 认证、同步、通知、迁移和更新的独立复核
- 主代理已有方案后的盲点检查

深度审查应指定少量相关文件；接近超时时按子系统拆分调用，不应持续提高单次超时。
`max` 不是普通审查默认值，调用时仍受单次 `$0.50` 预算和 240 秒超时限制。

不适合调用：

- 可以用 `rg` 或测试直接确认的仓库事实
- 简单格式修改
- 让 Claude 与 Codex 同时编辑相同文件
