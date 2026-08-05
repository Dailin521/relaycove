# AI 开发状态

> 本页只记录当前状态和交接信息。产品范围以工程落地方案为准，历史证据见 `docs/ai/tasks/`。

## 当前状态

- **当前阶段：** M5-02 已完成 — 个人/小团队内部 RC 初版可运行；严格双 Windows UI 全矩阵作为 owner 接受的已知限制
- **当前分支成果：** 阶段 2 认证与管理员闭环、阶段 3 会话/成员 API、阶段 4 全部服务端消息切片、阶段 5 服务端/客户端 SignalR，以及阶段 6 账户隔离缓存、权威快照、Sync 页原子提交、HTTP single-flight、真实认证会话、DPAPI 凭据存储、持久会话恢复、单账户 runtime、本地未读与通知候选事务、read-through 安全上传、平台无关通知协调与撤权清理确认；阶段 7 Windows 原生通知、单实例授权路由、attention/托盘；阶段 8 production 账户组合、凭据清理 barrier、账户隔离会话列表、持续连接/总未读、双栏壳、有界消息列表、History/Around、渲染后 read-through、Text durable 发送、WindowActivated/Periodic 持续同步、Reply、消息复制/日期分割、安全链接、稳定新消息分割线，以及会话作用域提及候选、显式 picker、token 绑定和 durable 非空提及发送；阶段 9 全部附件纵向闭环；阶段 10 权限化中文/Unicode 正文与附件原名搜索的 Shared/Server API、客户端 Global/Current UI、Around-first 重新授权跳转及一次性高亮；M4-01 可复现 Linux x64 Server RC、M4-02 可复现 Windows Client 自包含 ZIP、M4-03 共享更新协议、确定性清单、外部自举 Updater 与真实 rc.6→rc.11 替换恢复，以及 M4-04 Server exact 托管、Client 检查/下载、optional/mandatory UI 和显式 Exit→Updater 交接均已完成
- **最近验证通过的状态：** stage-15 production `827f04a` 已完成 Server 网页管理、两字符账号兼容与 SQLite migration；最终 Full 1,613 项、发布包校验及独立安全/迁移复核通过。Server rc.17 与 Client rc.14 已在批准的 HTTPS 子路径运行，网页后台、`lq`/`dal` 普通账号、重启会话保持与既有更新/Hub 边界均完成真实验证
- **可构建状态：** `已验证` — Fast、Full 与 Release 构建均为 0 警告、0 错误
- **自动化验证：** `已验证` — stage-15 最终 Full、format 与 1,613 项测试（Shared 69、Server 352、Client 1,154、Updater 38）通过；Release 0 警告/0 错误。覆盖网页管理 Cookie/JWT 隔离、CSRF、限流、实时撤权、PathBase、管理写操作、两字符账号 migration 与既有全部 RC 闭环
- **同步契约文档验证：** `已验证` — 固定 `ReviewHead=66ea70465741b4810e944d729d6374223c672bcc` 的规范断言、旧口径、文件白名单、空白与 Codex 降级独立复核通过
- **Claude MCP：** `已验证` — 本机全局 0.5.0 API-only 持久 job 健康检查、start/check/read 与重启可恢复状态目录可用；仓库访问限于 Read/Glob/Grep
- **最近 Claude 调用：** #85 已通过 MCP 0.5 持久任务完成 M5 更新供应链与备份恢复边界的唯一一次 Sonnet/High 只读挑战；其高风险意见均已由 `e200da6` 修正，两路 Codex 与本机验证复核通过，剩余要求进入 VPS 实测
- **Codex 项目配置：** `已验证` — 已移除仓库中遮蔽全局配置的旧 Claude MCP v0.3 `consult_claude` override；`codex mcp get claude_second_brain` 现解析到全局 0.5.0 的 start/list/check/read 持久工具，Fast/Explorer/Reviewer 项目设置保留

## 进行中

- 无阻塞开发切片。stage-15 已完成并部署；Windows 管理入口暂作为回退保留，等待 owner 实际使用网页后台后再决定是否移除。

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
- loopback 单跳可信转发头、认证限流真实客户端分区与公网伪造隔离（`DEC-054`）
- 内部完整便携 ZIP、双层 manifest、外部自举 Updater 与同卷替换恢复边界（`DEC-055`）
- 可复现 Linux x64 Server/migration bundle、USTAR + SHA-256、systemd/Nginx/config、离线 fail-closed 验收与部署恢复文档
- 可复现 Windows x64 self-contained Client ZIP、manifest/SHA-256、离线 fail-closed 验收、双构建确定性与真实发布目录启动/单实例验证
- 严格更新清单/SemVer/决策、确定性 generator、包内独立 self-contained Updater、精确进程等待、同卷 staging/backup/journal 恢复与真实 rc.6→rc.11 自举升级验证
- Server exact manifest/artifact 只读托管、Client 更新检查/受控下载/optional 与 mandatory 门禁、显式 Exit→Updater 交接和安全 smoke（`DEC-056`）
- Windows 内最小管理面、账号逻辑退役与 HTTP/SignalR token 代际、频道软删除、状态和持久上传上限（`DEC-057`）
- Nginx token 日志隔离、root-only 更新发布、完整可恢复备份和受控 HTTPS 子路径部署边界（`DEC-058`）
- Server rc.15 / Client rc.14 exact 产物、真实 VPS/TLS/systemd、受控备份恢复、公网更新完整性、真实 WPF 登录/实时接收与 optional/mandatory→Updater 升级 Gate；内部 RC 初版按 owner 指令接受未执行严格双 Windows UI 全矩阵的限制
- Server 内置 `/relaycove/admin/` 中文管理面、独立 Cookie/CSRF/JWT 隔离、生产 PathBase 与持久密钥；rc.17 已在香港 VPS 完成真实管理员登录、管理写入、重启会话保持及 `lq`/`dal` 普通账号登录验证
- 用户名下限按 `DEC-060` 从 3 放宽到 2，SQLite CHECK migration、客户端提及规则、旧库数据保留、单字符拒绝与回滚备份边界均已验证

## 下一任务

owner 可直接使用网页后台；确认稳定后可用独立小切片移除 Windows 管理入口。公开 main/Tag/Release 与严格第二 Windows UI 矩阵继续作为可选后续。

## 阻塞项

- 无。Claude 仅由主代理对每项重大决策调用一次，默认 Sonnet/High；调用前仍未解决的高风险争议才在该次选择 Opus/XHigh。子代理与普通代码审查不调用，所有意见仍必须由 Codex 以仓库和本机自动化复核。
