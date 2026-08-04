# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** 阶段 10 — 搜索
- **当前分支成果：** 阶段 2 认证与管理员闭环、阶段 3 会话/成员 API、阶段 4 全部服务端消息切片、阶段 5 服务端/客户端 SignalR，以及阶段 6 账户隔离缓存、权威快照、Sync 页原子提交、HTTP single-flight、真实认证会话、DPAPI 凭据存储、持久会话恢复、单账户 runtime、本地未读与通知候选事务、read-through 安全上传、平台无关通知协调与撤权清理确认；阶段 7 Windows 原生通知、单实例授权路由、attention/托盘；阶段 8 production 账户组合、凭据清理 barrier、账户隔离会话列表、持续连接/总未读、双栏壳、有界消息列表、History/Around、渲染后 read-through、Text durable 发送、WindowActivated/Periodic 持续同步、Reply、消息复制/日期分割、安全链接、稳定新消息分割线，以及会话作用域提及候选、显式 picker、token 绑定和 durable 非空提及发送；阶段 9 全部附件纵向闭环；阶段 10 权限化中文/Unicode 正文与附件原名搜索的 Shared/Server API、客户端 Global/Current UI、Around-first 重新授权跳转及一次性高亮已完成
- **最近验证通过的状态：** 客户端搜索 production `87b4af63a9ee5d3ae1c3df3c2f416f51fa30e929` 与认证失效修正 `f85c5442cdaa42d690eead32dffda5b20006f4df` 已通过 Search 107/107、WPF 搜索展示 10/10、账户壳 85/85、Fast/Full 1,426/1,426、两路独立复审、真实 Release WPF lifecycle、model drift、依赖漏洞、format 与空白检查；待本次交接提交后仅快进合入 `agent/v1-integration`
- **可构建状态：** `已验证` — 当前 Full 的 Release 构建为 0 警告、0 错误
- **自动化验证：** `已验证` — 当前最终 Fast/Full、format 与 1,426 项测试（Shared 41、Server 283、Client 1,101、Updater 1）通过；Search 定向 107/107、WPF 搜索展示 10/10、账户壳 85/85。覆盖服务端权限/Unicode/附件/LIKE/snippet/限流，以及客户端 scope、refresh/错误分类、迟到结果、撤权/注销同步清理、Around-first、虚拟化高亮与 UIA 脱敏；真实登录视觉/Narrator、VPS 与双客户端保持未验证
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 本机全局 0.5.0 API-only 持久 job 健康检查、start/check/read 与重启可恢复状态目录可用；仓库访问限于 Read/Glob/Grep
- **最近 Claude 调用：** `运行中` — 客户端搜索撤权/迟到结果/Around-first 决策唯一一次 #80 已通过本机 Claude Code 2.1.221 后台持久任务 `213daa77` 以 Sonnet/High 发起，仓库只读且工具限于 Read/Glob/Grep；主线继续并行，完成后读取并由 Codex 本地裁定
- **Codex 项目配置：** `已验证` — 已移除仓库中遮蔽全局配置的旧 Claude MCP v0.3 `consult_claude` override；`codex mcp get claude_second_brain` 现解析到全局 0.5.0 的 start/list/check/read 持久工具，Fast/Explorer/Reviewer 项目设置保留

## 进行中

- M3 客户端搜索代码、复核和自动化已完成；当前 `agent/stage-10-search-ui` 正在完成证据交接并仅快进绿色集成分支。Claude #80 按非阻塞策略后台运行，完成后读取；仍不读取 VPS 配置。

## 已完成

- 产品定位、第一版边界和工程落地方案
- 公开仓库、README、MIT License 与基础 `.gitignore`
- AI 工作流、任务模板、状态页、关键决策索引与独立审查模板
- Codex 全局与项目 Fast 默认；Claude Second Brain 仅由主代理按重大决策单次调用，默认 Sonnet/High、调用前仍未解决高风险时该次才选择 Opus/XHigh；Terra High Explorer 与 Sol High Reviewer
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
- production 单账户组合、自动恢复/登录/重试/注销、权威缓存后 activation lease、真实 activity/托盘边界状态、最小 WPF 账户壳、凭据清理 barrier 与无障碍 live region（`DEC-032`）
- 权威门控会话列表、提交后状态信号、持续连接/总未读、版本化旧 runtime 隔离和虚拟化双栏壳（`DEC-033`）
- 有界消息 cache 页面、History/Around 原子 merge、稳定撤权、版本化消息选择、虚拟化滚动和已应用视口后的 read-through（`DEC-034`）
- Text 严格验证、durable pending/失败恢复、单次幂等 POST、响应/回声同一行提升、显式原键重试和 WPF 输入状态（`DEC-035`）
- 账户级 WindowActivated 上升沿、五分钟 Periodic 背压调度与旧 scope 终止收敛（`DEC-036`）
- Reply、消息复制/日期分割，以及安全链接识别与显式打开（`DEC-037/038`）
- selection 冻结未读边界、分页证明精确位置与新消息分割线（`DEC-039`）
- 会话作用域最小提及候选、与发送授权同构的 Public/Private/Direct 查询、字面前缀与有界结果（`DEC-040`）
- 客户端显式提及 picker、正文 token 存活语义、selection/context 竞态门和 durable 规范 ID 集合（`DEC-041`）
- 认证单附件多层有界 streaming、随机非公开物理存储、事务元数据与崩溃残留恢复（`DEC-042`）
- 附件集合幂等 payload、INSERT 后 attach-once、完整消息投影、当前会话授权下载与 DB-first 未绑定 lease（`DEC-043`）
- 客户端 v2 `LocalAttachments` 原子迁移、严格远端元数据验证、消息同事务入库/完整回读、冲突与撤权级联（`DEC-044`）
- 客户端非幂等流式上传、unbound reservation、pending 原子绑定、durable Image/File retry 与独立上传 client（`DEC-045`）
- WPF 原生附件多选、路径内存边界、exact composer 草稿门与真实 content-copy 进度（`DEC-046`）
- exact FileDrop、文本优先 Ctrl+V、STA PNG 编码、25/100 MiB 双预算与 owner-safe 单飞取消（`DEC-047`）
- 强 ETag 全量可信下载、账户隔离 1 GiB 受控缓存、原子发布后 SQLite CAS、同 scope 撤权/配额协调与启动双向恢复（`DEC-048`）
- exact flight/context 提交、持久 WPF 附件状态、受控目录定位、pinned 内容复验与无锁 Windows shell（`DEC-049`）
- 内置 PNG/JPEG 有界解码、账户 scope 并发/超时所有权、最终授权提交与 WPF 缩略图/单查看器生命周期（`DEC-050`）
- Restricted Zone 临时副本、三阶段 STA job、Windows Attachment Manager 关联应用打开与受控 cleanup（`DEC-051`）
- 权限内嵌的消息/附件字面搜索、唯一消息限额、Unicode-safe snippet 与按 subject 零排队限流（`DEC-052`）
- 客户端搜索 runtime/request/result identity lease、Around-first 重新授权与 recycling-safe UI-only 高亮（`DEC-053`）

## 下一任务

完成 M3 绿色集成后进入 M4，先冻结并交付可复现的 RC 发布自动化与发布材料；真实登录视觉、VPS 与双客户端 Gate 保留到 M5。

## 阻塞项

- 无。Claude 仅由主代理对每项重大决策调用一次，默认 Sonnet/High；调用前仍未解决的高风险争议才在该次选择 Opus/XHigh。子代理与普通代码审查不调用，所有意见仍必须由 Codex 以仓库和本机自动化复核。
