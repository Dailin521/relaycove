# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** 阶段 8 — production 账户组合与聊天 UI
- **当前分支成果：** 阶段 2 认证与管理员闭环、阶段 3 会话/成员 API、阶段 4 全部服务端消息切片、阶段 5 服务端/客户端 SignalR，以及阶段 6 账户隔离缓存、权威快照、Sync 页原子提交、HTTP single-flight、真实认证会话、DPAPI 凭据存储、持久会话恢复、单账户 runtime、本地未读与通知候选事务、read-through 安全上传、平台无关通知协调与撤权清理确认；阶段 7 Windows 原生通知传输、固定 AppInstance 单实例、完整激活转交、账户/权限 fail-closed 路由、单轮提示音/任务栏闪烁及托盘关闭隐藏/彻底退出已完成
- **最近验证通过的状态：** 桌面 attention/托盘最终代码检查点 `93e4740e69049d97d4f9d0871862d80fecb8e740` 已随完成记录仅快进到绿色集成头 `de15ef589402050ee1072bd3c7ee6c41e3c07b9c`
- **可构建状态：** `已验证` — 当前 Full 的 Release 构建为 0 警告、0 错误
- **自动化验证：** `已验证` — Fast/Full、format、629 项测试、Client 418/418、桌面/通知定向 Release 280/280、复审补丁定向 39/39、activation filter 60/60 与压力 600/600；真实 Windows 优雅交接 30 轮×10 竞争者、冷/运行中/交接后 COM callback、并发冷启动/继任者/强杀恢复、静音 production builder payload/Register/Show/GetAll/Remove、极早关闭隐藏/同 HWND 恢复/托盘彻底退出、真实 MessageBeep/FlashWindowEx Start/STOP、WPF 非阻塞生命周期、既有本地缓存/HTTP/认证/SignalR/服务端回归、model drift、八项目漏洞审计与空白检查通过
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 本机全局 0.5.0 API-only 持久 job 健康检查、start/check/read 与重启可恢复状态目录可用；仓库访问限于 Read/Glob/Grep
- **最近 Claude 调用：** `已验证` — #46 本机后台 job `819c9403` 使用 Claude Code 2.1.220、实际 `claude-opus-5`/XHigh、Read/Glob/Grep 只读，对固定代码检查点复审 740546 ms 后返回 `PASS`，无 P0/P1。四项非阻断 P2 已由 Codex 复算并在 `93e4740` 补齐诊断、测试、取消会话结束闭锁恢复与限制记录；Claude 仍只作第二意见
- **Codex 项目配置：** `已验证` — Desktop 自带 Codex `0.146.0-alpha.3.1` Doctor 与 MCP 配置检查通过

## 进行中

- `agent/stage-8-production-account-shell`：接入 production 认证/恢复、单账户 runtime、通知授权 lease、真实连接状态和最小登录/账户壳；会话/消息 UI 留给后续独立切片。

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
- 本地消息来源/活动快照、权威未读派生、cursor 安全 read-through 与事务候选（`DEC-026`）
- 会话真实消息 read-through、receipt/快照双权威收敛、快照级退避与撤权竞态门禁（`DEC-027`）
- 旧通知状态收养、权威静音、串行通知协调、generation round gate、durable 平台清理确认与撤权重试（`DEC-028`）
- Windows App SDK 2.3.1 unpackaged 原生通知、账户隔离 Tag/Group、严格分号激活参数、注册就绪门、有界原生调用与不确定提交精确恢复（`DEC-029`）
- 固定 AppInstance key、完整 activation redirect、继任者回收、通知注销/key 释放顺序、授权路由与进程内去重（`DEC-030`）
- Toast 后同步轮共享 attention gate、静音原生 Toast、MessageBeep/FlashWindowEx STOP 所有权、NotifyIcon 状态与关闭隐藏/彻底退出（`DEC-031`）

## 下一任务

完成 production 账户组合、自动恢复、登录/注销/重试与最小账户壳，再接入账户隔离会话列表和持续未读/连接状态。

## 阻塞项

- 无。Claude 不设固定次数上限但只用于关键审查；所有采纳项仍必须由 Codex 以仓库和本机自动化复核。
