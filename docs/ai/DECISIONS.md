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

### DEC-006：自托管 access JWT 与一次性 refresh 轮换边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 工程方案要求封闭自托管 WPF/Server 直接以用户名和密码登录并签发 token；这不是通用 OAuth/OIDC authorization server，仍必须抵抗 JWT 算法混淆、账号/token oracle、refresh 并发重放、SQLite 锁竞争和机密日志泄漏。
- **决策：** 单服务 v1 access token 使用 HS256，Base64 signing key 至少 32 个随机字节，只能由 User Secrets/环境或受控部署配置注入，缺失或畸形时启动失败。JWT 固定 `typ=at+jwt`、HS256、issuer、audience、`sub/jti/iat/exp`，`MapInboundClaims=false`，access 有效期 15 分钟、clock skew 30 秒；验证后必须从数据库确认用户仍存在且未禁用，不信任 token 中的可变权限。所有认证时间来自把 `TimeProvider` 截断到毫秒 UTC 的单一 `ServerClock`。
- **决策：** refresh 原始值是强类型 32 字节 CSPRNG Base64Url，hash 是不同强类型；任何 `ToString()`、错误和日志都不得暴露原始值或 hash。refresh 有效期 30 天，并在默认非 deferred/Serializable SQLite 写事务内以第一条条件更新原子撤销旧 token，只有受影响行数为 1 才插入新 token 并提交；并发 loser、畸形、未知、过期或已撤销 token 对 refresh 统一为 `401 AuthenticationFailed`。残余 `SQLITE_BUSY/LOCKED` 返回 `503 ServiceUnavailable`。v1 不增加 token family，不因已撤销 token 重放而全账号登出。
- **决策：** login 只把 body 形状错误作为 `400 ValidationFailed`；非法字符用户名仍执行 dummy verify 后统一 401。logout 对畸形、未知、过期、已撤销和有效 token 均无响应体返回 204，有效 token 尽力撤销。JWT challenge/forbidden 分别使用 `AuthenticationRequired`/`AccessDenied`，不在 `WWW-Authenticate` 暴露失败原因；未处理异常使用 `InternalServerError`，认证存储暂不可用使用 `ServiceUnavailable`。`RateLimitExceeded`、`ServiceUnavailable`、`InternalServerError` 是 `DEC-004` 后新增稳定码。
- **决策：** login 按实际 `RemoteIpAddress` 使用 10 次/分钟 fixed window，refresh 使用 60 次/分钟，均不排队，拒绝返回 429、`Retry-After` 和稳定 envelope；未知地址进入固定 sentinel 分区，当前不读取客户端转发头。登录/refresh/rehash 必须用领域方法在同一成功事务单调推进相应用户活动时间与 `UpdatedAt`。
- **理由：** 固定 token 类型、算法和 claims 消除默认映射与算法混淆；短期 access 加每请求用户状态检查使禁用即时生效；条件写而非 read-modify-write 让 refresh rotation 在单库中可证明单赢；强类型和统一响应降低误存明文与枚举风险；端点级限流限制高成本密码尝试且不影响已认证 API。
- **影响：** appsettings 可以提交 issuer/audience/生命周期和限流非机密值，但不得提交 signing key；开发和测试必须显式注入临时 key，生产由部署 Gate 提供。无 token family、无 `kid`/key rotation、单进程限流、反代前共享 IP 和 15 分钟内 access 不可主动撤销是已知 v1 限制；可信代理、分布式限流、密钥轮换与重放族追踪另行决策。
- **来源：** 工程落地方案第 8.2、10.2、11.1、18.4、阶段 2；`docs/ai/tasks/2026-08-03-stage-2-auth-endpoints.md`；2026-08-03 ASP.NET Core 10 JWT bearer/rate limiter、Microsoft.Data.Sqlite transaction 文档；RFC 8725、RFC 9700。

### DEC-007：一次性管理员引导、密码策略与动态管理员授权

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 阶段 2 需要在不开放注册的前提下创建首个管理员并允许管理员创建账号；若仓库携带默认凭据、启动时覆盖既有账号、信任 JWT 中的旧角色或在授权检查后不再确认 actor，都会形成持久后门或 TOCTOU 提权窗口。
- **决策：** `BootstrapAdmin` 默认关闭且仓库不提供用户名、密码或占位机密。启用时完整凭据只能由 User Secrets、环境或受控部署配置注入；缺失、畸形，或关闭开关但仍注入凭据时启动失败且不得回显密码。运维必须先显式应用 migration；短 `IHostedService.StartAsync` 在服务监听前以 scoped DbContext 和默认非 deferred/Serializable SQLite 事务检查整个 Users 表，只在零行时创建一个管理员。已有任意用户时一律 no-op，不创建、覆盖、提权或改密；schema 缺失、锁定或写入失败使启动失败。成功后运维必须移除凭据并关闭开关。
- **决策：** 新密码统一由共享 `PasswordPolicy` 验证：按 Unicode scalar value 计数 15–128 个字符，允许空格和 Unicode，不要求大小写、数字或符号组合；拒绝控制字符、常见弱密码以及与用户名、昵称或 `RelayCove` 相同或直接派生的上下文密码。完整原始字符串交给 IdentityV3 hasher 和 verifier，不截断、不写日志，也不在本兼容切片改变既有密码的 Unicode 规范化语义。v1 使用小型内置弱密码集合与上下文规则，不声称覆盖完整泄漏语料库。
- **决策：** `POST /api/admin/users` 接受用户名、昵称、密码和 `IsAdmin`，成功返回不含密码/哈希的用户响应；结构/密码错误为 `400 ValidationFailed`，规范化用户名冲突为稳定 `409 UserNameAlreadyExists`。管理员 policy 以 scoped EF handler 每次从数据库确认 bearer `sub` 对应用户仍存在、未禁用且 `IsAdmin=true`；服务层在 SQLite 写事务内再次确认 actor 后才检查唯一名、哈希密码并插入。并发同名创建只允许一个成功，其他请求归一化为 409；审计日志只记录 actor/user ID、角色标志和结果，不记录请求对象、用户名、昵称、密码或哈希。
- **理由：** 空表而不是“没有管理员”作为唯一引导条件，使遗留普通账号不能被配置意外升级；外部一次性凭据与显式 migration 避免仓库后门和隐式 schema 变更。动态授权加事务内复核使禁用/降权可即时收敛；长口令、无组合规则和弱密码拒绝与当前 NIST 指南一致，同时不引入外部身份或大型联网依赖。
- **影响：** 没有用户且未启用 bootstrap 的服务可以启动但无法登录，属于明确运维状态；非空但无管理员的库不会自愈，必须通过受控数据恢复流程处理。禁用、删除、改角色、重置密码、用户列表、首次改密、完整泄漏密码服务和管理员 UI 仍属于阶段 11/部署后续任务，不得借本决策提前实现。
- **来源：** 工程落地方案第 7.1、8.2、17.4、18.2、阶段 2；`docs/ai/tasks/2026-08-03-stage-2-admin-bootstrap.md`；2026-08-03 NIST SP 800-63B-4 password verifier、ASP.NET Core 10 hosted service 与 authorization handler DI 官方文档。

### DEC-008：会话成员存储、Direct 唯一身份与删除边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 工程方案给出 Conversations/ConversationMembers 基本字段，但未定义一对一会话如何在并发和软删除后保持单例、Direct 名称如何面向两个用户展示、成员角色数值或用户/会话硬删除时的引用行为；若留到 API 层再决定，会产生无法由数据库阻止的重复历史线程和迁移兼容问题。
- **决策：** `ConversationType` 固定为 PublicChannel=1、PrivateChannel=2、Direct=3；会话内 `ConversationMemberRole` 固定为 Member=1、Administrator=2，与全局 `Users.IsAdmin` 分离。频道名称为 1–100 个有效 Unicode scalar value且拒绝控制字符/全空白；Direct 数据库 `Name` 固定为空，未来 DTO 按当前用户动态使用另一参与者昵称。`LastReadMessageId` 为非负 64 位整数，实体只允许单调推进；加入/重新加入时仍按 `DEC-003` 由服务写事务传入当前会话最大消息 ID，当前无消息时为 0。
- **决策：** Conversations 增加内部可空 `DirectParticipantKey`：两个不同参与者的小写标准 D GUID 按 ordinal 排序并以冒号连接；仅 Direct 必填，唯一索引覆盖软删除行。软删除后重新发起同一对话必须恢复原会话，而不是创建第二条线程。数据库 CHECK 固定类型、名称/key 对应关系、GUID/毫秒 UTC、更新时间顺序、布尔值、角色和非负已读；ConversationMembers 使用 `(ConversationId, UserId)` 复合主键并按 UserId 建索引。
- **决策：** 会话正常删除只设置 `IsDeleted`。创建者是必填 User 外键且使用 Restrict，避免硬删用户静默删除会话历史；成员行是会话从属数据，会话硬删或成员用户硬删时级联。Direct 恰好两个 Member、创建者属于参与者以及加入初始化的消息水位是跨表/未来 Messages 不变量，必须由后续阶段 3 Serializable 写事务验证，不能声称当前 CHECK 已覆盖。
- **理由：** 永久 canonical pair key 把并发单例交给唯一索引并保留一对一历史连续性；Direct 名称动态派生避免同一字段无法同时表达双方视角；显式 Restrict/Cascade 防止 EF 必填关系的默认级联误删会话，同时保留从属成员清理。没有提前创建 Messages 表或触碰同步协议。
- **影响：** 后续创建/获取 Direct 必须 INSERT-first 或在等效写事务中处理唯一冲突，并在发现软删除记录时恢复同一 ID；频道/成员 API 必须维护会话角色与全局管理员的区分。阶段 11 用户硬删除必须先处理其创建者引用，常规账号删除宜通过现有 `IsDisabled` 收敛。未来 Attachments migration 加 Avatar FK 前仍需处理孤儿值。
- **来源：** 工程落地方案第 7.2、7.3、10.1、11.1、11.2、阶段 3；`DEC-003`、`DEC-005`；`docs/ai/tasks/2026-08-03-stage-3-conversation-storage.md`；2026-08-03 Microsoft EF Core 10 SQLite limitations、keys/indexes 与 cascade delete 官方文档。

### DEC-009：会话 API、动态管理授权与权威读取边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 阶段 3 需要把三种会话的创建、可见性和成员管理落为稳定 HTTP 契约，同时避免全局管理员越权读取私有内容、授权检查与写入之间的 TOCTOU、Direct 并发重复，以及不完整会话列表触发客户端误删缓存。
- **决策：** `POST /api/conversations` 是判别请求：Public/Private 频道只接受 `Type` 与 `Name`，且仅数据库当前全局管理员可创建；Direct 只接受 `Type=Direct` 与 `ParticipantUserId`，任意正常认证用户可创建或获取。频道创建者写入会话内 `Administrator`；Direct 使用 `DEC-008` 永久 canonical pair key，新建返回 201，已存在或恢复软删除行返回 200，并始终保持同一 ID 和恰好两个 `Member`。禁止自聊，目标用户必须存在且未禁用。
- **决策：** 私有频道成员写操作允许写事务内仍为全局管理员的用户，或该频道内当前 `Administrator`；全局管理覆盖只授予成员管理权，不自动授予私有会话内容读取权。成员 POST 是幂等 upsert：新增为 201，已存在 no-op 或角色更新为 200；DELETE 对不存在成员仍返回 204。Public 的访问对正常认证用户隐式成立，不伪造 JoinedAt 成员清单；Direct 成员不可变。对 Public 成员清单或非 Private 成员写返回稳定 `409 ConversationTypeConflict`。
- **决策：** `GET /api/conversations` 在单个非分页权威 SQL 查询中按当前身份投影完整可见集合并返回 `Complete=true`；Public 对全部正常认证用户可见，Private/Direct 只对当前成员可见，Direct 名称按当前用户投影另一参与者昵称。单会话读取把权限过滤与 DTO 投影放在同一查询中。未知、删除或不可访问会话统一 fail-closed 为 `403 ConversationAccessRevoked`，不提供存在性 oracle；普通成员尝试管理返回 `403 AccessDenied`。私有成员清单只对当前成员或全局管理员可读，Direct 清单只对参与者可读。
- **决策：** 会话和成员命令使用 SQLite 非 deferred、Serializable 写事务，在修改前重新读取 actor、目标用户、会话类型与当前角色。当前尚无 Messages，创建/重新加入成员的 `LastReadMessageId` 为 0；阶段 4 引入 Messages 时，必须在同一成员写事务中改为读取该会话当前 `MAX(Messages.Id)`，重复添加现有成员不得重置水位。当前 DTO 的 `LastMessageId`、`UnreadCount` 和无显式成员的 Public `LastReadMessageId` 均为 0。
- **理由：** ASP.NET Core 的资源授权发生在资源加载后，需要命令/查询边界内的命令式动态判断；SQLite 同时只有一个待提交写者，非 deferred 事务可在权限复核前取得写锁，避免先授权后等待写锁造成的陈旧权限。EF Core 单查询避免 split query 在并发撤权下返回内部不一致结果，且 `Complete=true` 只用于确实完整的集合。统一 403 隐藏私有资源是否存在，409 则只表示已获授权上下文中的资源类型不支持该操作。
- **影响：** Shared 增加创建、列表、会话和成员 DTO，以及 `ConversationTypeConflict`、`UserNotFound` 稳定错误码；Server 端点和测试必须覆盖 Direct 正反序/并发/恢复、事务内动态降权、撤权后 403、单查询全集和日志脱敏。阶段 4 修改成员初始化时必须与 Messages migration/服务同批验证，不能继续写常量 0。频道更新/删除、公共显式成员、用户目录、消息、SignalR 和客户端不在本切片。
- **来源：** 工程落地方案第 7.2、7.3、8.2、10.2、阶段 3；`DEC-003`、`DEC-006`、`DEC-008`；`docs/ai/tasks/2026-08-03-stage-3-conversation-api.md`；2026-08-03 Microsoft.Data.Sqlite transactions、ASP.NET Core resource-based authorization 与 EF Core single/split query 官方文档。
