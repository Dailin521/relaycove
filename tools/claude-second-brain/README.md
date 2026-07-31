# Claude Second Brain MCP

一个项目内的只读 stdio MCP，将本机 Claude Code CLI 作为 Codex 的独立“第二大脑”。

## 能力与边界

MCP 只暴露 `consult_claude`：

- 支持 `analysis`、`review`、`challenge`、`brainstorm` 四种视角。
- 默认使用 Claude `opus`、`high` effort。
- 仓库访问开启时，Claude 只能使用 `Read`、`Glob`、`Grep`。
- 不开放 Bash、编辑、写文件、Chrome 或会话持久化。
- 默认单次预算上限 `$0.50`，Claude 调用超时 240 秒；Codex MCP 工具总超时 300 秒。两者都可通过项目配置调整。
- 结果同时返回请求模型和 Claude CLI 报告的实际模型；两者不一致时设置 `model_mismatch=true`，不得假定别名一定被上游采用。

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

项目级配置位于 `.codex/config.toml`。Codex 通常需要新建任务或重启客户端后才能加载新 MCP。

## 使用建议

适合调用：

- 重要架构取舍的反方论证
- 认证、同步、通知、迁移和更新的独立复核
- 主代理已有方案后的盲点检查

深度审查应指定少量相关文件；接近超时时按子系统拆分调用，不应持续提高单次超时。

不适合调用：

- 可以用 `rg` 或测试直接确认的仓库事实
- 简单格式修改
- 让 Claude 与 Codex 同时编辑相同文件
