# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** 阶段 1 — 认证、会话、权限与核心消息闭环
- **当前分支成果：** 阶段 2 认证与管理员闭环、阶段 3 会话/成员 API、阶段 4 全部服务端消息切片、阶段 5 服务端/客户端 SignalR，以及阶段 6 账户隔离缓存、权威快照、Sync 页原子提交、HTTP single-flight、真实认证会话、DPAPI 凭据存储、持久会话恢复和单账户 runtime 已完成
- **最近验证通过的状态：** 单账户 runtime 与账户切换生命周期代码检查点 `e2195cd6835b9d858c00f1757e7fc7d640ea1021`
- **可构建状态：** `已验证` — 当前 Full 的 Release 构建为 0 警告、0 错误
- **自动化验证：** `已验证` — Fast/Full、format、382 项测试、真实磁盘 AccountScope/SQLite、HTTP Sync、客户端登录/启动恢复/持久轮换/logout、真实 Windows DPAPI、单账户 runtime 启动/重连/在飞行终止/切换所有权、客户端与服务端 SignalR、既有消息/会话/认证回归、model drift、八项目漏洞审计与空白检查通过
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 本机全局 0.5.0 API-only 持久 job 健康检查、start/check/read 与重启可恢复状态目录可用；仓库访问限于 Read/Glob/Grep
- **最近 Claude 调用：** `已验证` — #32 持久审查请求 Opus/XHigh，实际 `claude-sonnet-5` 且 `model_mismatch=true`；有效发现已落实为 retry 补拉、显式 flight 线性化和在飞行终止测试。用户已取消固定调用上限，后续仅关键用途调用，Codex 仍为主
- **Codex 项目配置：** `已验证` — Desktop 自带 Codex `0.146.0-alpha.3.1` Doctor 与 MCP 配置检查通过

## 进行中

- `agent/stage-6-account-runtime`：单账户 runtime、启动/重连、logout、Dispose 和账户切换顺序已完成并验证，待快进集成。

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
- Text 消息不可变存储、AUTOINCREMENT、INSERT-first 幂等发送、单查询权限化 keyset History、会话未读/最大消息聚合、私有成员加入水位与 `DEC-010`
- read-through 真实目标验证、单调服务端确认、Public 个人水位、Private/Direct 撤权边界与 Public 成员 API 隔离（`DEC-011`）
- 消息 around 专用双侧窗口、目标归属、两侧更多标志与撤权 fail-closed 查询（`DEC-012`）
- 固定上界 Sync、deferred SQLite 只读快照、动态权限过滤、权限空洞与单调权威游标（`DEC-013`）
- SignalR 认证 Hub、`sub` 用户标识、每连接权威分组、当前收件人快照和提交后 NewMessage 尽力投递（`DEC-014`）
- 私有成员真实删除提交结果、目标用户全部连接 ConversationAccessRevoked、并发一次事件与发布失败隔离（`DEC-015`）
- 客户端动态 token SignalR、确定连接状态、默认重连与 NewMessage/撤权 FIFO 单一 sink（`DEC-016`）
- AccountScopeId 隔离本地 SQLite、权威登记门、统一消息合并、durable revocation intent/tombstone/fatal fail-closed 与安全原生 SQLite pin（`DEC-017/018/019`）
- Complete 权威会话快照对账、重新加入解封与 Sync 页/LastSyncCursor 原子提交（`DEC-020`）
- 客户端 Sync HTTP 有界重试、一次 refresh、精确游标 block 与账户 single-flight（`DEC-021`）
- 客户端真实登录、内存认证会话、refresh rotation 与 logout 线性化（`DEC-022`）
- Windows CurrentUser DPAPI 单一 refresh 凭据文件与原子发布（`DEC-023`）
- 持久 refresh 会话恢复、可信轮换提交边界、凭据清理与单会话所有权门（`DEC-024`）
- 单账户 runtime、Realtime→Startup Sync、显式 flight 线性化与账户切换终止所有权（`DEC-025`）

## 下一任务

完成账户 runtime 后，实现未读/通知协调、窗口前后台触发与 Windows 通知可靠性入口。

## 阻塞项

- 无。本机 Claude MCP 0.5.0 已完成持久只读审查；不设固定次数上限但只用于关键审查，Codex 继续以仓库和自动化证据为主推进。
