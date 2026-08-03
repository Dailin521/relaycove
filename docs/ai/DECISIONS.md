# 关键决策索引

只记录会影响架构、公共协议、数据库或兼容性的决定。普通实现细节、任务过程和聊天内容不得写入本页。若决策发生变化，新增记录并链接被替代项，不直接改写历史。

## 记录格式

| 字段 | 内容 |
| --- | --- |
| ID | `DEC-NNN` |
| 状态 | `提议 / 已接受 / 已替代` |
| 日期 | `YYYY-MM-DD` |
| 背景 | 需要解决的问题与约束 |
| 决策 | 选择的方案 |
| 理由 | 为什么采用该方案 |
| 影响 | 对接口、数据、部署或兼容性的影响 |
| 来源 | 工程方案章节、Issue 或 PR |

## 已接受决策

### DEC-001：采用轻量单体与本地存储

- **状态：** 已接受
- **日期：** 2026-07-31
- **背景：** RelayCove 面向约 20 人的小团队，需要低成本自托管和个人可维护性。
- **决策：** 使用 WPF 客户端、ASP.NET Core 单体服务、服务端与客户端 SQLite、VPS 本地附件目录。
- **理由：** 减少部署组件和运维复杂度，优先保证可靠消息闭环。
- **影响：** 第一版不引入 Redis、消息队列、Elasticsearch、对象存储、微服务或 Kubernetes。
- **来源：** 工程落地方案第 3、4、22 节。

### DEC-002：持久化与实时推送分离

- **状态：** 已接受
- **日期：** 2026-07-31
- **背景：** SignalR 连接可能中断，不能作为唯一可靠消息来源。
- **决策：** 消息通过 HTTP API 幂等写入 SQLite，成功持久化后再由 SignalR 推送；客户端重连后按游标补拉并去重。
- **理由：** 保证消息不丢、可重试、可补拉且不会重复显示或通知。
- **影响：** 任何消息实现都必须保持“先入库、后推送”，并维护客户端同步游标和去重记录。
- **来源：** 工程落地方案第 4.2、12、13 节。

### DEC-003：消息同步、幂等与通知语义

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-002` 确立了“先入库、后推送”，但尚未定义权限过滤造成的全局 ID 空洞、并发重放、pending 身份、断线补拉事务、通知恢复和私有频道撤权如何共同收敛。
- **决策：** 第一版在单服务端实例、单 SQLite 主库、写事务串行化和消息不可变前提下，使用服务端解释的 `LastSyncCursor` 与固定消息 ID `SnapshotUpperBound` 分页。服务端按当前权限扫描并返回 `NextCursor`；客户端验证响应后以逐页本地事务合并消息与推进游标，所有账户状态由规范化服务器地址和用户 ID 派生的 `AccountScopeId` 隔离。
- **决策：** 本地消息以自增 `LocalId` 为主键、以可空唯一 `ServerMessageId` 保存服务端身份；Realtime、Sync、History、SendResponse 共用一个合并函数，并明确区分 `Inserted`、`PendingPromoted`、`Duplicate`、`Conflict`。服务端发送采用 INSERT-first：新建 `201`、相同载荷重放 `200`、相同幂等键不同载荷 `409 IdempotencyKeyReuse`；只有新建事务提交后尝试一次推送，失败由周期同步补偿。
- **决策：** `IsNotificationHandled` 是唯一逐消息通知真源，单实例内只有一个串行 `NotificationCoordinator`。同步轮次用原子 gate 合并 Realtime 与 Sync 候选，按 `SyncReason` 执行 `None`、`PerMessage` 或 `Summary`；平台接受 Toast 后、落本地状态前的崩溃窗口按 at-least-once 处理，不宣称严格 exactly-once。
- **决策：** 私有频道当前成员可通过 History/Search 懒加载全部历史，不设置加入前历史可见水位。`ConversationMembers.LastReadMessageId` 只表示单调已读边界；加入或重新加入事务以当前会话最大消息 ID 初始化该边界。撤权由尽力实时事件、`Complete=true` 权威会话全集和稳定 `403 ConversationAccessRevoked` 收敛，客户端先持久化 tombstone 并 fail-closed，再执行可重试缓存和通知清理。
- **理由：** 这些规则让权限空洞、并发请求、四种到达顺序、崩溃恢复和撤权迟到帧都有唯一可编码结果，同时保持第一版轻量单体边界，不引入 outbox、消息队列或第二种数据库。
- **影响：** `SyncResponse` 固定为 `Messages`、`NextCursor`、`SnapshotUpperBound`、`HasMore`；本地表、错误码、通知激活目标和后续契约测试必须遵循上述语义。增加服务端写实例、更换数据库、允许消息编辑删除或改变 ID/提交顺序时，必须新增决策并重新设计同步协议。
- **来源：** 细化 `DEC-002`；工程落地方案第 4.2、10、11、12、13、20、21 节；`docs/ai/tasks/2026-07-31-stage-0-sync-contract.md`。

### DEC-004：稳定 API 错误与认证机密日志边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** Client 和 Server 需要共享可兼容的失败分支；登录 DTO 又包含密码与 Token，C# record 默认字符串表示会把构造参数带入日志。
- **决策：** API 失败使用 `ApiErrorResponse(Code, Message, TraceId, Details)`。`Code` 是稳定字符串兼容键，`Message` 仅供人类诊断或显示，字段错误使用 Web camelCase 键。第一批代码为 `ValidationFailed`、`AuthenticationFailed`、`AuthenticationRequired`、`AccessDenied` 以及 `DEC-003` 的 `SyncCursorInvalid`、`IdempotencyKeyReuse`、`ConversationAccessRevoked`。
- **决策：** 未知用户、密码错误和账号禁用对外统一为 `401 AuthenticationFailed`，避免账号枚举。Login request/response 覆盖 record `ToString()`，密码、Access Token 和 Refresh Token 一律显示 `[REDACTED]`；错误响应、TraceId 与日志也不得携带这些机密。
- **理由：** 字符串码便于跨版本、跨语言稳定分支；把诊断文本与机器码分离可以修改或本地化提示而不破坏客户端。统一认证失败和默认脱敏降低枚举与日志泄露风险。
- **影响：** 后续 Controller、客户端 HTTP 层、测试和文档必须按 HTTP 状态与 `Code` 组合判断，不得解析 `Message`。新增或替代公共错误码需要在对应任务更新本决策或新增替代决策。
- **来源：** 工程落地方案第 8.2、10.2、18.4 节；`docs/ai/tasks/2026-08-03-stage-1-auth-contracts.md`。

### DEC-005：认证存储规范化、时间与机密哈希边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** Users/RefreshTokens 首个 SQLite schema 必须在 Windows 开发机与 Linux VPS 上保持相同身份键、时间比较和机密存储语义；SQLite 不执行 `MaxLength`，`DateTimeOffset` 排序受限，Unicode 大小写又会随全球化后端变化。
- **决策：** v1 登录名只允许 3–64 个 ASCII 字母、数字、点、下划线和连字符；保留原始 `UserName`，由同一实体方法同步生成 invariant-uppercase `NormalizedUserName`，唯一性覆盖禁用用户和全部历史行。GUID 统一为小写 `D` 文本；时间统一为固定 24 字符 UTC 文本并拒绝非 UTC 写入。密码使用 DI 配置的 ASP.NET Core IdentityV3 `PasswordHasher`（100000 iterations），包装器归一化畸形输入并保留 rehash-needed，不向领域边界暴露 Identity 枚举。refresh token 原始值未来固定为 32 字节 CSPRNG，数据库只存 `Base64Url(SHA-256(raw bytes))` 的 43 字符确定性哈希，v1 不加 pepper。首个迁移由显式运维/部署动作应用，应用启动不得隐式改库。
- **理由：** ASCII 登录标识配合 Unicode `DisplayName` 避免 ICU/NLS、不可见和双向控制字符造成跨环境账号漂移；固定文本格式使 SQLite 字典序等于 UTC 时间序；框架版本化密码格式支持升级重哈希；高熵 refresh token 的快速确定性哈希既能按值查找又不暴露原文。
- **影响：** schema 增加内部 `NormalizedUserName`；关键长度、格式、布尔值和非空要求必须用 SQLite CHECK 验证，不能依赖 `HasMaxLength`。测试必须真实运行 migration up/down、`HasPendingModelChanges`、外键/级联、UTC Kind/比较和约束冲突。`AvatarAttachmentId` 在 Attachments 切片前只保留可空文本，不建外键；后续迁移加 FK 前必须处理孤儿值。默认管理员、保留用户名、密码策略、Token 签发/轮换/保留和 WAL/备份另行决策。
- **来源：** 工程落地方案第 7.1、11.1、11.2、18.4、19.4、阶段 2；`docs/ai/tasks/2026-08-03-stage-2-auth-storage.md`；2026-08-03 Microsoft EF Core SQLite 与 PasswordHasher 官方文档。
