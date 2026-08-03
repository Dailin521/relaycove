# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** 阶段 1 — 认证、会话、权限与核心消息闭环
- **当前分支成果：** 阶段 2 认证与管理员闭环、阶段 3 会话/成员存储及访问 API 已合入；阶段 4 文字消息纵向切片已开始
- **最近验证通过的状态：** 会话访问与成员 API 代码 `b9b004109183e0157bca5c16f0acdaf7a39c8940`
- **可构建状态：** `已验证` — 当前 Full 的 Release 构建为 0 警告、0 错误
- **自动化验证：** `已验证` — Full、format、134 项测试、会话 HTTP/Direct/成员并发/撤权/单查询/busy、真实 SQLite migration/约束、认证轮换/bootstrap、JWT 负向、限流、日志泄漏、发布依赖、model drift、漏洞审计与空白检查通过
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 11 个单元测试以及 RelayCove、`oss-maintainer-hub` 真实只读 MCP 调用通过
- **最近 Claude 调用：** `未验证` — 文字消息 API XHigh challenge #22 对 `ChallengeHead=e677597` 因本机认证源覆盖 claude.ai 登录在 60 秒内超时；未取得审查结论且按用户要求未重试，`DEC-010` 由 Codex 结合仓库与 SQLite/EF Core 官方证据独立收敛
- **Codex 项目配置：** `已验证` — Desktop 自带 Codex `0.146.0-alpha.3.1` Doctor 与 MCP 配置检查通过

## 进行中

- `agent/stage-4-text-message-api`：冻结并实现文字消息存储、幂等发送、keyset 历史和会话聚合。

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
- 默认关闭的空库管理员 bootstrap、15–128 Unicode scalar 密码策略、动态数据库管理员授权、管理员创建用户与 `DEC-007`
- Conversations/ConversationMembers 实体、Direct 永久唯一身份、成员角色/单调已读、SQLite 约束与 migration（`DEC-008`）
- 会话创建/详情/权威全集、Direct 并发获取/恢复、私有成员动态管理、撤权 403 与 `DEC-009`

## 下一任务

开始阶段 4 文字消息存储、HTTP 幂等发送、History/Sync 与会话权限基础闭环。

## 阻塞项

- 无。Claude 候选 MCP 当前受本机认证源配置影响，但不阻塞已有本地证据或下一任务的仓库调查。
