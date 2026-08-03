# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** 阶段 1 — 认证、会话、权限与核心消息闭环
- **当前分支成果：** 同步契约、v1 外层账本、.NET 10 可构建骨架、认证共享契约/存储及 login/refresh/logout/me HTTP 闭环已完成
- **最近验证通过的状态：** 认证端点代码 `b72194a168ba99a5268661df8f8cac7c48578fc4`
- **可构建状态：** `已验证` — 当前 Full 的 Release 构建为 0 警告、0 错误
- **自动化验证：** `已验证` — Fast/Full、format、73 项测试、真实 SQLite migration/并发轮换/锁库、JWT 负向、限流、日志泄漏、发布依赖、漏洞审计与空白检查通过
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 11 个单元测试以及 RelayCove、`oss-maintainer-hub` 真实只读 MCP 调用通过
- **本任务 Claude 调用：** `部分验证` — 两次 safe-mode 只读 `claude-opus-5` / XHigh challenge 对 `ChallengeHead=87ff08a` 返回 `REVISE`，有效发现已纳入 `DEC-006`；候选 MCP review 因本机认证源覆盖 claude.ai 登录而未取得结论，已按规则降级为 Codex 固定差异自审
- **Codex 项目配置：** `已验证` — Desktop 自带 Codex `0.146.0-alpha.3.1` Doctor 与 MCP 配置检查通过

## 进行中

- 无；认证端点切片等待仅快进合入 `agent/v1-integration`。

## 已完成

- 产品定位、第一版边界和工程落地方案
- 公开仓库、README、MIT License 与基础 `.gitignore`
- AI 工作流、任务模板、状态页、关键决策索引与独立审查模板
- Codex 全局与项目 Fast 默认；用户级 Claude Opus XHigh Second Brain（按次支持 Max）；Terra High Explorer 与 Sol High Reviewer
- 消息同步固定上界分页、INSERT-first 幂等、统一本地合并、通知恢复和私有频道撤权契约（`DEC-003`）
- v1 外层执行状态、绿色集成头、Claude 预算、阻塞和用户 Gate 账本
- .NET 10 解决方案、四个源项目、四个测试项目及真实 Fast/Full 验证脚本
- 登录 DTO、统一 API 错误 envelope、稳定错误码、敏感 `record` 日志边界与 `DEC-004`
- Users/RefreshTokens、首个真实 SQLite 迁移、ASCII 用户名规范化、IdentityV3 密码服务、refresh-token hash-only 存储与 `DEC-005`
- 严格 typed HS256 access JWT、login/refresh/logout/me、原子 refresh rotation、动态禁用检查、认证限流、统一错误 envelope 与 `DEC-006`

## 下一任务

定义并实现阶段 2 的默认管理员引导与管理员用户生命周期最小纵向切片；复用当前认证协议，不提前实现客户端 UI 或消息功能。

## 阻塞项

- 无。Claude 候选 MCP 当前受本机认证源配置影响，但不阻塞已有本地证据或下一任务的仓库调查。
