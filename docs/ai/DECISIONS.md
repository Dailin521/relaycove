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

### DEC-010：不可变文字消息、幂等入库与历史分页边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-003` 已冻结消息不可变、权限先于幂等回读和固定 ID 游标，但尚未定义首个可写 Message schema 的文本边界、外键删除、提及/回复语义、目标唯一冲突后的事务恢复，以及 History 页形状。若这些由各端点临时决定，会产生键复用误判、撤权绕过、ID 复用和历史翻页遗漏。
- **决策：** `MessageType` 固定为 Text=1、Image=2、File=3、System=4；首个用户发送端点只开放 Text，其他已定义类型返回稳定 `409 MessageTypeUnsupported`。Text 按原字符串精确保存和比较，要求 1–4000 个有效 Unicode scalar value、至少一个非空白字符；允许 TAB/CR/LF，拒绝其他 Unicode Control，不 trim、不做 Unicode 或换行规范化。请求/响应 record 的 `ToString()` 必须隐藏正文和集合载荷，服务日志只记录 actor、conversation、message、client-message ID 与结果。
- **决策：** Messages.Id 使用 SQLite `INTEGER PRIMARY KEY AUTOINCREMENT`，已提交 ID 永不复用且允许空洞。消息一经提交不编辑、不撤回、不删除；Conversation 硬删 Cascade 消息，Sender 使用 Restrict，Reply 使用 NO ACTION（单独删除被回复消息仍失败，同时允许一条 Conversation 硬删语句在语句末完成整组级联），MessageMention 随 Message Cascade、MentionedUser 使用 Restrict。Reply 必须大于 0 且属于同一 Conversation。MentionUserIds 是最多 20 个非空、无重复的无序集合，目标必须是正常用户且当前可访问该会话；AttachmentIds 在附件存储上线前必须为空。
- **决策：** `POST /api/messages` 在 SQLite 非 deferred Serializable 写事务内先用 `ConversationAccessQuery` 复核当前权限，再验证 reply/mentions，然后尝试目标 INSERT；不得在 INSERT 成功前更新 Conversation 或产生其他持久副作用。只识别 `UNIQUE(SenderId, ClientMessageId)` 冲突；失败实体从 tracker 移除后在同一事务和发送者范围回读。会话、类型、精确正文、reply、附件集合与 mention 集合全部相等返回 200，不同返回 `409 IdempotencyKeyReuse`；新建消息成功后才更新 Conversation.UpdatedAt 并返回 201。撤权后旧键重放仍先返回 `403 ConversationAccessRevoked`。
- **决策：** History 使用唯一消息 ID keyset：`beforeMessageId` 可空且排除边界，`limit` 默认 50、范围 1–100；数据库按 ID 降序取 `limit+1`，响应消息按 ID 升序。`HasMore=true` 时 `NextBeforeMessageId` 等于本页最旧 ID，否则为 null。权限过滤、ConversationId 和消息投影处于同一权威查询边界；不使用 offset。首个消息切片上线时，会话列表投影真实 LastMessageId，并按当前成员水位统计他人未读；Public 尚无状态行时水位为 0。
- **决策：** Messages 上线的同一变更必须将私有成员首次加入/重新加入的常量 0 替换为事务内该会话当前 `MAX(Messages.Id)`；重复 upsert 不得重置现有水位。around、read-through、Search、Sync、SignalR、附件与客户端缓存仍由后续切片实现，不以占位结果冒充。
- **理由：** AUTOINCREMENT 是 `DEC-003` 固定游标不复用的数据库前提；精确字符串与集合比较给并发重放唯一答案。权限先行和 INSERT 前无副作用封住撤权与失败插入窗口；ID keyset 对不可变消息稳定且有匹配索引，避免 offset 在并发新增下的漂移。
- **影响：** 新 migration 必须验证旧认证/会话数据升级降级、AUTOINCREMENT 删除后不复用、目标唯一、CHECK、自引用/用户/会话外键与硬删行为。Shared/Server 测试必须固定 201/200/409、相同键并发、撤权旧键、精确正文/mention 集合、keyset 页边界和日志脱敏。允许消息编辑删除、增加写实例或更换数据库时必须新增决策并重审 `DEC-003`。
- **来源：** 工程落地方案第 7.4、10.1/10.2、11.1/11.2、12.1–12.4、阶段 4；`DEC-002`、`DEC-003`、`DEC-009`；`docs/ai/tasks/2026-08-03-stage-4-text-message-api.md`；2026-08-03 SQLite AUTOINCREMENT、EF Core 10 SQLite value generation/keyset pagination 与 Microsoft.Data.Sqlite transactions 官方文档。

### DEC-011：read-through 目标验证与 Public 个人状态行

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-003` 要求已读边界单调推进且不得接受任意极大 ID。Private/Direct 已有成员行可保存水位；Public 对所有正常用户隐式可见，但不会为每位读者预建或公开成员清单，同时权威会话 DTO 已从 actor 的 ConversationMember 读取个人水位。
- **决策：** `POST /api/conversations/{conversationId}/read` 使用 `MarkConversationReadRequest(MessageId)`，成功返回 `ConversationReadReceipt(ConversationId, LastReadMessageId)`。服务在 SQLite 非 deferred Serializable 写事务内先用 `ConversationAccessQuery` 复核当前权限，再验证正数目标消息真实属于该会话，最后保存并确认 `MAX(old, requested)`；权限失败先于消息目标回读，未知/删除/撤权统一 403，跨会话、不存在或任意过大目标统一 400。
- **决策：** Private/Direct 只更新当前已有成员。Public 正常用户没有状态行时，在首次有效 read-through 中创建 `ConversationMemberRole.Member` 的内部个人状态行，`JoinedAt` 使用当前服务时间、`LastReadMessageId` 使用已验证目标；已有行只调用单调推进。该行不改变 Public 隐式可见性，Public 成员 list/管理仍返回 `ConversationTypeConflict`，read-through 不触碰 Conversation.UpdatedAt。
- **理由：** 复用已经承载 actor 水位和生命周期级联的表可避免仅为 Public read state 增加重复 schema；目标存在性验证阻止伪造未来水位；同一立即写事务串行化并发 read、首次 Public 行创建和 Private 撤权，使返回确认具备明确线性化顺序。
- **影响：** 新增 Shared read 请求/确认、Server read 服务与 endpoint，不新增 migration。around、Sync、SignalR receipt 与客户端 pending read-through 仍由后续切片实现。
- **来源：** 工程落地方案第 7.4、12.6；`DEC-003`、`DEC-009`、`DEC-010`；`docs/ai/tasks/2026-08-03-stage-4-message-read-api.md`；当前 ConversationAccessQuery、ConversationMember 与 SQLite 写事务实现。

### DEC-012：消息 around 窗口、目标错误与撤权边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 搜索跳转需要按目标消息拉取有限上下文，但工程方案只有 `messages/around/{messageId}?before=20&after=20` 路由，没有冻结响应、窗口范围、目标错误、更多标志或查询期间撤权语义。直接复用 History 游标会混淆“向前翻页”和“双侧定位”两种协议。
- **决策：** around 响应固定为 `MessageAroundResponse(Messages, TargetMessageId, HasMoreBefore, HasMoreAfter)`；`before`/`after` 默认各 20、各允许 `0..100`，0 表示该侧不返回上下文但仍准确报告是否存在更多。响应必须包含真实目标恰好一次，前文取 ID 小于目标的最近 N 条，后文取 ID 大于目标的最近 N 条，最终按 ID 严格升序；双侧标志分别表示对应返回窗口外仍有消息。
- **决策：** 非正目标或窗口越界为 `400 ValidationFailed`。服务先以 `ConversationAccessQuery` 判断当前会话内容访问并在同一授权投影中确认目标；未知、删除、不可访问或撤权会话统一 403，只有已获访问的会话内不存在/跨会话目标才返回 400。最终有限 MessageDto/mention 投影再次绑定当前权限并必须包含目标，否则按撤权 fail-closed 为 403；全局管理员成员管理覆盖仍不授予私有内容读取权。
- **理由：** 专用双侧响应让客户端无需从不足一页猜测是否还能加载上下文；允许零窗口支持只定位目标。权限优先避免用目标 ID 探测私有会话，最终查询重检则在不持有跨请求快照或扩大写锁的前提下封住查询间撤权窗口；消息不可变和 ID 永不复用使目标在两次只读查询之间不会合法消失。
- **影响：** 新增 Shared around 响应及 Server 验证、endpoint、查询与测试，不新增 migration。Search、固定上界 Sync、客户端跳转/高亮和附件仍是后续切片；允许消息编辑删除、改变 ID 语义或增加写实例时必须重审该边界与 `DEC-003`。
- **来源：** 工程落地方案第 10.2、12.2–12.4、15.5、阶段 4；`DEC-003`、`DEC-009`、`DEC-010`、`DEC-011`；`docs/ai/tasks/2026-08-03-stage-4-message-around-api.md`；当前 MessageQueryService、ConversationAccessQuery 与 Message/MessageMention 模型。

### DEC-013：固定上界 Sync 的默认页与 SQLite 只读快照

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-003` 已冻结固定上界、权限空洞和游标不变量，但没有指定可空 limit 的缺省值，也未落到当前 Microsoft.Data.Sqlite/EF Core 如何保证首页最大 ID 与本页处于同一数据库快照。使用既有立即写事务会让纯同步读取不必要地争抢写锁，使用两个无事务查询又可能让首页上界与页面观察不同提交点。
- **决策：** `GET /api/sync` 的 limit 默认 100、允许 `1..200`。每次请求显式开启 Microsoft.Data.Sqlite `deferred: true`、Serializable 事务并交由当前 DbContext 使用；在该只读快照内先读取仍正常的 actor 与全局 `MAX(Messages.Id)`，完成游标/上界验证，再执行按消息计数限制为 `limit+1` 的权限化页面与 mentions 投影。续页同样读取当前最大 ID以拒绝伪造未来上界，但仍使用客户端原样携带的有效上界。
- **决策：** 每页权限过滤为：未删除 Public；当前成员的 Direct；当前成员的 Private 且 `MessageId > actor.LastReadMessageId`。Private 过滤只影响增量 Sync，不改变 History/around 的全部历史读取。事务中 actor 已不存在或禁用时返回认证失败，不得以空成功页推进游标。读事务不执行写入、升级或跨 HTTP 请求保留。
- **理由：** 100 条缺省页在 200 上限内提供确定兼容行为；deferred 只读事务既给两条查询一致快照，又避免立即取得 SQLite RESERVED 写锁。先对消息做 `Take(limit+1)` 再连接 mentions，防止多 mentions 消耗消息页容量；末页由服务端直接推进到快照上界以跨过权限空洞。
- **影响：** 新增 Shared SyncResponse、Server Sync endpoint/验证/服务及自动化测试，不新增 migration 或依赖。客户端事务合并、AccountScopeId、通知 gate、SignalR 和同步世代仍属后续切片；更换数据库、增加写实例或改变消息 ID/可变性时必须重审 `DEC-003` 与本决策。
- **来源：** 工程落地方案第 12.4；`DEC-003`、`DEC-009`、`DEC-010`、`DEC-011`；`docs/ai/tasks/2026-07-31-stage-0-sync-contract.md`；`docs/ai/tasks/2026-08-03-stage-4-message-sync-api.md`；本地 Microsoft.Data.Sqlite 10.0.5 transaction API 与当前 EF Core SQLite 模型。

### DEC-014：SignalR 身份、组与提交后尽力实时投递

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 阶段 5 需要在不削弱 `DEC-003/010` 持久化与幂等真源的前提下缩短在线消息延迟。SignalR principal 在连接期间缓存，组只属于连接且不是安全机制；浏览器 WebSocket/SSE 还必须以查询参数携带 access token，默认 Hosting Information 日志会记录完整 URL。若直接向旧会话组广播，会让连接后被撤权的用户继续收到新敏感内容；若在事务内或幂等回放时推送，又会产生未提交/重复消息。
- **决策：** 服务端只暴露要求认证的 `/hubs/chat` 强类型 Hub，当前不提供客户端可调用的业务或加组方法。JWT bearer 只在该 Hub 路径提取非空 `access_token` 查询值；`Microsoft.AspNetCore.Hosting` 日志最低为 Warning，生产连接必须使用 HTTPS。SignalR 用户标识固定为已验证 JWT `sub` 的非空标准 `D` GUID，Hub endpoint 在 token 到期时关闭连接。
- **决策：** 每个新连接从数据库重新读取当前正常 actor 可见的未删除 Public 及其成员 Private/Direct 会话并逐连接加组；断线重建不继承旧组，组名由会话 GUID 确定且客户端不能自选。组仅作路由优化。`NewMessage` 每次使用单个权威数据库查询计算当前正常收件人：Public 的全部正常用户、Private/Direct 的当前成员，然后通过 SignalR user ID 向每位用户的所有连接发送完整 `MessageDto`，包含发送者连接。
- **决策：** 只有 `MessageCommandService` 返回 `Created` 后 endpoint 才调用发布，此时 SQLite 事务已提交；`Replay` 和任何失败状态都不发布，并发同键仍只有插入获胜者发布一次。发布不使用已取消的 HTTP request token；收件人查询或 SignalR transport 的任意异常在发布边界内被吸收，只记录 message/conversation ID、已解析收件人数和异常元数据，不记录正文、昵称或 token，不改变已经决定的 201，也不在请求内重试。实时投递是尽力而为，遗漏由固定上界 Sync 补偿。
- **理由：** 以数据库收件人快照而非可能陈旧的组状态裁决每次敏感投递，能保证撤权提交后开始的发布不再选择该用户，同时接受撤权前已排队帧属于既有客户端 deny-set 威胁模型。提交后状态分支把一次实时事件绑定到唯一持久行；失败隔离保持 HTTP 与数据库为可靠真源。Hub 限域查询 token 与 Hosting 日志过滤封住最直接的凭据泄露路径。
- **影响：** Server 增加 ChatHub、`IUserIdProvider`、当前收件人发布器和 SignalR 注册；Server.Tests 增加同版本 Microsoft SignalR .NET client 依赖以运行真实连接测试，不新增产品运行时包或 migration。`ConversationAccessRevoked` 事件、主动移组/断连、客户端重连/同步/deny-set、跨实例 backplane/outbox 和真实 HTTPS/WebSocket 部署验收属于后续切片。
- **来源：** 工程落地方案第 10.3、12.1/12.2、12.5、阶段 5；`DEC-003`、`DEC-006`、`DEC-009`、`DEC-010`、`DEC-013`；`docs/ai/tasks/2026-08-03-stage-5-signalr-new-message.md`；2026-08-03 ASP.NET Core 10 SignalR authentication/authorization、security、users/groups 与 strongly typed Hub 官方文档。

### DEC-015：成员删除提交与实时撤权事件

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-003` 已固定 `ConversationAccessRevoked` 只是客户端尽快 fail-closed 的信号，`DEC-014` 已确保新消息不用陈旧组做授权；但当前私有成员 DELETE 无论成员是否存在都返回同一 204，endpoint 无法判断是否应发事件。若每次 204 都推送，会让重复/并发重试产生虚假撤权；若事务内推送，失败或回滚会让事件先于权威状态。
- **决策：** 私有成员删除命令保留现有非 deferred Serializable 权威写事务和外部 204/error 契约，但内部结果增加 `RemovedUserId`：只有事务内找到成员、删除保存并成功提交时为目标 GUID；幂等无成员的 204 以及全部失败状态为 null。Endpoint 仅在 `NoContent + RemovedUserId` 后调用发布，因此顺序或并发同目标删除只有真实提交的获胜者发布一次。
- **决策：** 强类型 Hub 客户端增加 `ConversationAccessRevoked(Guid conversationId)`。Publisher 直接使用标准 `D` 目标 user ID 向其所有现有连接发送，不读取当前活跃状态、不依赖或修改 conversation group；目标在撤权后禁用也仍可收到清理信号。发布使用独立于 request-abort 的取消边界，transport 任意异常被吸收，只记录 target/conversation ID 与失败元数据，不回滚删除、不改变 204、不重试。
- **理由：** 以事务返回的真实删除事实作为唯一发布资格，能把一次事件绑定到一次已提交权限变化，同时保持 DELETE 幂等。按用户路由覆盖多设备且无需不可靠的 server connection registry；组仍只优化未来路由。事件丢失、离线和撤权前在途帧继续由权威全集/403/Sync 与客户端 deny-set/tombstone 收敛。
- **影响：** 修改 ConversationCommandService 的内部返回形状、Conversation endpoint 和强类型 Hub 契约，增加撤权 publisher/transport 与真实连接测试；不改变 HTTP/数据库/Shared 契约，不新增 migration 或依赖。客户端 purge、主动移组/断连、重新加入解封和真实 WebSocket 验收仍为后续切片。
- **来源：** 工程落地方案第 10.3、12.8、阶段 5；`DEC-003`、`DEC-009`、`DEC-014`；`docs/ai/tasks/2026-08-03-stage-5-signalr-access-revoked.md`；当前 ConversationCommandService、ConversationEndpoints 与 SignalR user routing 实现。

### DEC-016：客户端实时连接生命周期与串行事件入口

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 服务端 `DEC-014/015` 已提供提交后 NewMessage 和成员撤权事件，但 WPF 客户端仍是空骨架。若连接层自行缓存 token、直接改 UI/数据库或并发调用多个消费者，会把认证轮换、事件顺序、撤权 fail-closed 和 UI 线程混在一个不可测边界；若把 SignalR 的默认重连误当成初始连接或永久重试，又会隐藏明确的不可用状态。
- **决策：** 客户端使用与服务端一致的 `Microsoft.AspNetCore.SignalR.Client 10.0.10`，只接受无 user-info/query/fragment 的绝对 HTTP(S) 服务端基址并组合固定 `hubs/chat`。`AccessTokenProvider` 每次从调用方读取当前 token，不在连接/状态/日志中缓存或暴露 token。公共客户端状态采用工程方案既定数值：Disconnected=0、Connecting=1、Connected=2、Reconnecting=3、ServerUnavailable=4。
- **决策：** 显式 Start 映射 Connecting→Connected；初始失败映射 ServerUnavailable 并向调用者保留异常。已建立连接启用 SignalR 默认 0/2/10/30 秒自动重连，Reconnecting/Reconnected 映射为 Reconnecting/Connected；尝试耗尽或非主动 Closed 为 ServerUnavailable，主动 Stop/Dispose 为 Disconnected。连接层不隐藏无限重启，后续账户与同步 orchestrator 可显式再次 Start；Reconnected 后的权威会话对账和 Sync 由状态消费者触发。
- **决策：** `NewMessage`、`ConversationAccessRevoked` 与状态变化只进入一个 FIFO 串行 sink，撤权处理完成前不处理随后入队的消息。sink 在后台异步线程执行，不假设 WPF Dispatcher；下一层适配器负责 UI marshal。单次 sink 异常只记录事件种类、message/conversation ID、状态与异常元数据并继续消费，不记录 token、正文、显示名或用户名；撤权的安全处理仍要求后续 sink 先更新 deny-set/tombstone 再做可失败清理。
- **理由：** 动态 token provider 允许 refresh 后的新 HTTP/重连请求取新凭据；显式状态区分初始失败、短暂重连、永久不可用和主动停止，避免 UI 展示虚假在线。单一串行入口为 Realtime、未来 Sync/History/SendResponse 的唯一合并路径提供确定顺序，并封住撤权事件之后迟到消息越过清理的竞争窗口。
- **影响：** Shared 增加 ConnectionState；Client 增加 SignalR 运行时依赖、实时 sink/连接组件和真实内存 Hub 测试。不实现认证 UI、本地数据库、deny-set、Sync 或 MainWindow 接线；这些后续消费者必须复用该 sink 边界而非旁路注册 Hub handler。改变事件顺序、重连策略或 token 生命周期时必须新增决策重审。
- **来源：** 工程落地方案第 3.1、10.1、10.3、12.3–12.8、阶段 5；`DEC-003`、`DEC-006`、`DEC-014`、`DEC-015`；`docs/ai/tasks/2026-08-03-stage-5-client-realtime.md`；2026-08-03 ASP.NET Core 10 SignalR .NET client、HubConnection events 与 AccessTokenProvider 官方文档。

### DEC-017：账户作用域本地 SQLite 与撤权 fail-closed 顺序

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-016` 已把实时事件串行化，但客户端尚无本地权限状态。若 Realtime 自动创建未知会话、撤权先删库后更新内存，或 tombstone 失败后继续展示缓存，撤权前在途帧就能重新插入敏感内容；若服务器/用户共用数据库，又会造成跨账户数据泄漏。
- **决策：** 本地稳定作用域严格使用工程方案既定 `AccountScopeId = Base64UrlNoPadding(SHA256(UTF8(CanonicalServerBaseUri + "\n" + CurrentUserId.D-lower)))`。Canonical URI 只接受无 user-info/query/fragment 的绝对 HTTP(S)，规范 scheme/IDN host/default port/dot segments/尾斜杠并保留反向代理子路径。数据库与缓存位于显式绝对 root 下的单一 scope 子目录，不记录用户名或 token。
- **决策：** 使用 `Microsoft.Data.Sqlite 10.0.10`、每次操作独立 connection、foreign keys、WAL、默认私有 cache、参数化 SQL和有界 timeout；不共享 ADO.NET 对象，不以伪 async API 阻塞 UI。当前 schema/version 只建立实际使用的 LocalConversations、LocalMessages、LocalMessageMentions、RevokedConversations 和 LocalAppState；写入通过进程内 scope gate 与 SQLite 事务串行。
- **决策：** Realtime 只能向已由权威会话 DTO 显式登记的 conversation 合并，未知 ID 拒绝并请求对账，绝不自动创建。消息以可空唯一 ServerMessageId 和 `(SenderId, ClientMessageId)` 识别 Inserted、PendingPromoted、Duplicate 或 Conflict；不可变载荷与 mentions 必须一致，事务提交后才通知上层。
- **决策：** 撤权处理第一步同步加入进程 deny-set，再取得 scope gate，以独立最小写事务先 upsert RevokedConversations tombstone、再删除 LocalConversation 并依靠外键清除消息/mentions。消息入口在触库前检查 fatal/deny-set，并在事务内重检 tombstone/会话，所以撤权完成后迟到帧不能复活。首次 tombstone 持久化失败时保留 deny-set并把整个 scope 标记 fatal fail-closed；本进程不得读、展示或合并该 scope，只有后续显式冷启动权威对账流程可恢复。
- **理由：** 内存先拒绝封住事件与 SQLite 之间的窗口，持久 tombstone 封住重启窗口，scope gate 与事务重检给撤权/消息竞争单一线性化顺序。账户目录隔离和未知会话拒绝防止跨账户或伪造实时事件扩张本地授权。
- **影响：** Client 增加本地 SQLite 依赖、scope/schema/store/realtime sink 与真实磁盘测试；不实现完整会话 HTTP、Sync、通知、附件或 UI。未来完整权威对账必须先提交后才允许清除 tombstone；数据库加密和离线远程擦除仍是第一版限制。
- **来源：** 工程落地方案第 11.3、12.3–12.8、阶段 6；`DEC-003`、`DEC-015`、`DEC-016`；`docs/ai/tasks/2026-08-03-stage-6-local-access-cache.md`；2026-08-03 Microsoft.Data.Sqlite connection strings、async limitations、transactions 与 database errors 官方文档。

### DEC-018：持久撤权意图与冷启动缓存授权门

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-017` 的内存 deny-set 与单个 tombstone/清理事务能封住正常提交后的迟到消息，但若 tombstone 首次写入失败或进程在事务提交前终止，内存 fatal/deny 状态会随进程消失；本阶段又不实现 Complete=true HTTP 对账，不能让新 store 自动展示磁盘旧会话。撤权回调还可能在连接关停时收到已取消 token，不能因此丢掉安全工作项。
- **决策：** 撤权同步加入当前账户 scope 的线程安全 deny-set 后，不再接受调用方取消；先在 `LocalAppState` 独立立即写事务提交 `RevocationIntent/<conversationId>`，再用第二个立即写事务 upsert tombstone、删除 LocalConversation（两级外键级联消息与 mentions）并清除 intent。初始化先重放所有 intent，再加载 tombstone；重放失败把 scope 置为 fatal。写事务使用 `BEGIN IMMEDIATE`，busy/locked 按有界次数重放整个事务。
- **决策：** 新建 store 的当前进程授权集合始终为空，即使磁盘已有 LocalConversations，也不得读取或合并，直到调用方以本轮当前权威 `ConversationDto` 显式登记。登记在同一写事务内先检查 tombstone，命中时拒绝且不清 tombstone/deny-set；未知 Realtime 只触发对账请求。读取和消息写入在触库前与事务内都重检 fatal/deny/tombstone/权威会话，Conflict 零写入回滚，唯一键判定固定为 ServerMessageId 后 `(SenderId, ClientMessageId)`。
- **理由：** durable intent 把“已经收到撤权但尚未完成清理”变成可重放事实；冷启动授权门覆盖 intent 自身尚未提交即崩溃以及离线漏事件窗口，同时不假装本切片已实现 HTTP 对账。取消不可丢、固定事务顺序和读写共用门禁消除关停、竞争与 WAL 旧快照造成的 fail-open。
- **影响：** 不新增表或外部依赖，复用本切片实际使用的 LocalAppState；当前切片不提供 tombstone 清除/重新加入 API，后续 Complete=true 权威对账必须显式、原子地实现恢复流程。进程崩溃前尚未落盘的数据仍无法远程擦除，但不会在未重新权威登记时展示。
- **来源：** `DEC-003`、`DEC-016`、`DEC-017`；工程落地方案第 12.3、12.7、12.8；`docs/ai/tasks/2026-08-03-stage-6-local-access-cache.md`；本机 Claude #30 XHigh challenge；真实磁盘故障注入、重启重放、取消、竞争与账户隔离测试。

### DEC-019：客户端 SQLite 原生 bundle 安全版本覆盖

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-017` 选用 `Microsoft.Data.Sqlite 10.0.10`，其官方 NuGet 依赖范围允许 `SQLitePCLRaw.bundle_e_sqlite3 >= 2.1.11`，但默认最低解析为 2.1.11；仓库漏洞审计将其内含低于 SQLite 3.50.2 的原生库报告为 High（GHSA-2m69-gcr7-jv3q）。不能把已通过功能测试等同于依赖安全通过。
- **决策：** 保持 Microsoft.Data.Sqlite 10.0.10 和当前 ADO.NET/schema 设计不变，在 Client 直接固定同一既有传递包 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`，使 bundle/core/provider/lib 同步解析为 2.1.12；不得只压制 advisory。依赖升级后必须重跑真实磁盘测试、Full 与 `--vulnerable --include-transitive`。
- **理由：** 2.1.12 是同依赖家族的最小稳定补丁覆盖，满足 Microsoft.Data.Sqlite 的无上限最低版本范围，避免引入 3.x 大版本兼容风险，同时让审计实际解析到不受该 advisory 标记的原生包。
- **影响：** 增加一个显式 PackageReference 以固定原有传递依赖，不新增运行时能力或架构边界；后续 Microsoft.Data.Sqlite 若提升最低安全 bundle，可删除冗余直接 pin，但必须以当时依赖图和漏洞审计为证据。
- **来源：** `DEC-017`；`docs/ai/tasks/2026-08-03-stage-6-local-access-cache.md`；2026-08-03 NuGet `Microsoft.Data.Sqlite 10.0.10` 与 `SQLitePCLRaw.bundle_e_sqlite3/lib.e_sqlite3` 包元数据；GitHub Advisory GHSA-2m69-gcr7-jv3q；仓库真实 `dotnet list package --vulnerable --include-transitive` 输出。

### DEC-020：Complete 会话快照与 Sync 页原子本地提交

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 服务端固定上界 `/api/sync` 以及 `ConversationListResponse(Conversations, Complete=true)` 权威全集已完成，客户端账户缓存也已有唯一合并与撤权门，但 LocalCache 只能逐会话/逐消息各自提交，尚未消费完整快照或保存 LastSyncCursor。若客户端把部分列表当全集清理，或先推进 cursor 再逐条合并，会分别造成错误撤权和永久漏消息。
- **决策：** 复用阶段 3 既有 `ConversationListResponse`，且只允许 `Complete=true` 快照驱动缺失撤权。客户端先校验完整/唯一 DTO，在 scope gate 下为缺失会话先建立 deny 与 durable intent，再以一个立即写事务 upsert 当前集合、清除权威重新加入者的 tombstone、tombstone+删除缺失项并清 intent；提交后才替换当前 store 授权集合，任何失败使 scope fatal。
- **决策：** Sync 页在触库前校验固定上界全部响应不变量；本地立即写事务先确认磁盘 LastSyncCursor 等于调用方 expected cursor，再让本页每条消息调用与 Realtime 相同的事务内 merge 裁决，最后写 NextCursor。Inserted、PendingPromoted、Duplicate 可提交；Conflict、未知/撤权、陈旧 cursor 或协议错误整页回滚且不推进。后续页的 expected SnapshotUpperBound 必须与服务端响应完全相同，不能夹断、归零或另取上界。
- **理由：** 显式 Complete 位把破坏性“缺失即撤权”限定在可证明全集；单一本地事务使 cursor 成为已持久化消息集合的提交水位，而不是网络接收水位。复用同一 merge 裁决维持 Realtime 先到、发送回声与补拉的统一幂等语义。
- **影响：** 为既有 Shared wrapper 增加脱敏输出，新增 Client snapshot/page store API 和真实磁盘测试，不改服务端协议、数据库或 `/api/sync`。HttpClient retry、401 refresh、single-flight、后台触发和未读/通知在后续切片实现。
- **来源：** 工程落地方案第 12.3–12.8、阶段 6；`DEC-003`、`DEC-013`、`DEC-017`、`DEC-018`；`docs/ai/tasks/2026-08-03-stage-6-client-sync-page.md`；当前 ConversationEndpoints、SyncEndpoints、SyncResponse 与 AccountScopedLocalCache 仓库事实。

### DEC-021：客户端同步请求重试与账户 single-flight

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-020` 已使完整会话快照和单个 Sync 页具备本地原子语义，但客户端还没有 HTTP 循环。若不区分“同请求网络重试”与“下一轮新快照”，会在失败后悄然更换 upper；若每个 Reconnect/窗口/timer 都启动循环，会产生并发游标提交和无界补跑。
- **决策：** 一个 ClientSyncCoordinator 仅管理一个 AccountScopeIdentity，复用外部长生命 HttpClient，每次尝试新建请求并动态取当前 Bearer token。每轮先获取/提交 `Complete=true` 会话全集，再从磁盘游标开始：首页无 upper，续页必须原样使用首页 upper，只有每页本地事务成功才可请求下页。
- **决策：** 单个逻辑 HTTP 请求最多三次瞬态重试；网络/timeout/408/429/500/502/503/504 使用 250ms 起始、5s 封顶指数退避加有界抖动，合法 `Retry-After` 取更长值但不超过 30s。`401` 独立于瞬态计数，对被拒 token 只刷新一次并立即重试原请求；第二个 401 或刷新失败终止。`400`/非法 JSON/响应不变量为协议错误；只有 `409 + SyncCursorInvalid` 阻塞该 coordinator 后续请求，不归零游标、不删 pending。
- **决策：** `SyncReason` 数值固定为 Startup=1、Reconnect=2、WindowActivated=3、Periodic=4。并发触发共用当前 flight 并至多登记一次补跑；选择顺序为 WindowActivated > 未完成 Startup 恢复 > Reconnect > Periodic，补跑运行期间的新触发直接并入它，不链式生成第三轮。调用者取消只取消等待；账户 Dispose 才取消共享循环。
- **理由：** 固定请求参数和有界重试保留 `DEC-013/020` 的游标真源；长生命 HttpClient 复用连接池，动态 token 与一次 refresh 封住旧凭据重放；single-flight 把多触发变成确定的最多两轮，而不是并发或永不停止的循环。
- **影响：** Shared 增加 SyncReason，Client 增加同步 coordinator、最小认证会话契约、HTTP 分类/退避和脱敏结果，不新增依赖或服务端变更。账户组合根、真实 refresh token 安全存储、定时/窗口/SignalR 钩子、通知与受控游标重建 UI 由后续切片完成。
- **来源：** 工程落地方案第 12.4/12.5、阶段 6；`DEC-003`、`DEC-013`、`DEC-018`、`DEC-020`；`docs/ai/tasks/2026-08-03-stage-6-client-sync-orchestration.md`；当前 Shared 错误契约、SyncEndpoints 与 AccountScopedLocalCache；2026-08-03 .NET 10 HttpClient 与 Retry-After 官方文档。

### DEC-022：客户端 refresh rotation 与 logout 线性化边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 服务端 `DEC-006` 已采用单次使用 refresh token 原子轮换，客户端 Sync 也已要求被拒 access token 的 single-flight refresh。若客户端自动重试响应不确定的 refresh，服务端可能已经撤销旧 token 并签发新 token，但客户端重放旧 token 后只能得到 401；若 logout 与 refresh 并发，则可能注销旧 token 后又把新 token 发布回内存，形成表面退出但会话仍有效的竞争。
- **决策：** 客户端认证会话只在内存锁内保存原始 access/refresh token，任何公共结果、`ToString` 和日志均不暴露敏感身份或服务端地址。相同被拒 access token 的并发 refresh 使用锁内先发布的 TaskCompletionSource 合并为一次请求；调用者取消仅停止自己的等待，会话生命周期取消才取消共享请求。
- **决策：** login、refresh、logout 均不自动重试。refresh 与 logout 共用异步操作门；refresh 成功时原子替换成同一响应的两枚 token，401 或用户 ID 错配时清空会话，网络/429/5xx/协议失败不部分覆盖。logout 等待在途 refresh 后取得最新 refresh token，先清空本地会话再发幂等请求，远端失败或取消都不恢复本地状态。
- **理由：** RFC 9700 的 rotation/replay detection 安全性要求客户端尊重单次使用边界；响应丢失后的正确动作是显式重新认证，而不是猜测请求是否提交。单一操作门给 refresh/logout 一个确定顺序，先本地退出保证远端不可用时也不会继续使用凭据。
- **影响：** Client 新增真实登录和内存认证会话并直接实现 `IClientAuthenticationSession`；不修改 Shared/服务端协议或依赖。DPAPI、自动登录、主动 refresh、账户 scope 组合与 UI 留到后续独立切片。
- **来源：** `DEC-006`、`DEC-016`、`DEC-021`；工程落地方案第 3.1、8.3、12.4、阶段 6；`docs/ai/tasks/2026-08-03-stage-6-client-auth-session.md`；当前 AuthenticationEndpoints/AuthenticationSessionService；RFC 9700。

### DEC-023：CurrentUser DPAPI 单一 refresh 凭据文件

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-022` 的认证会话只在内存保存 token，进程退出后无法安全恢复；工程方案要求 DPAPI 本地加密。access token 仅 15 分钟且可重新签发，密码不应持久化，真正需要跨进程保存的是可轮换 refresh token 与其服务器/用户归属。若用 LocalMachine，本机其他账户也可解密；若直接覆盖目标文件，中断可能同时丢失新旧凭据。
- **决策：** 客户端只保存一个 versioned payload：canonical server base URI、非空 user ID 和 refresh token；不保存密码、access token、用户名、显示名或设备名。payload 使用固定应用/schema entropy 与 Windows DPAPI `DataProtectionScope.CurrentUser` 加密，明文 byte buffer 使用后清零；磁盘固定文件名和日志不含身份或服务器信息。
- **决策：** ciphertext 在同目录临时文件完整写入并 flush 后，已有目标使用 `File.Replace`，首次保存使用同卷 `File.Move` 发布；单实例内 Save/Load/Clear 串行。读取在解密前后都有大小上限，并严格校验 schema、canonical URI、user ID 和 refresh token；篡改、错误用户、截断或非法字段返回 Corrupt，不自动信任、删除或覆盖正式文件。Clear 幂等，但权限/I/O 失败必须返回失败。
- **理由：** CurrentUser 把可解密范围限制到当前 Windows 用户，DPAPI 自带完整性保护且无需应用持有密钥；只保存 refresh token 减少长期秘密面。先落 ciphertext 再原子发布使失败保留上一个完整轮换点，严格恢复验证避免损坏数据进入自动认证。
- **影响：** Client 增加 Windows-only 凭据 store 与真实 DPAPI/磁盘测试，不新增包、Shared/服务端协议、数据库或 migration。相同用户上下文中的恶意进程仍可能调用 DPAPI，离线设备也不能被远程擦除；自动 refresh、损坏提示与账户 runtime 在后续切片实现。
- **来源：** 工程落地方案第 3.1、9.3、阶段 6；`DEC-006`、`DEC-022`；`docs/ai/tasks/2026-08-03-stage-6-client-credential-store.md`；2026-08-03 Microsoft ProtectedData、DataProtectionScope、File.Replace 官方文档与本机 WindowsDesktop 参考程序集证据。

### DEC-024：refresh 轮换的内存发布与持久提交边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-022` 已保证单次 refresh 的内存原子轮换，`DEC-023` 已保证单个 DPAPI 文件的原子替换，但两者不是同一事务。服务端成功轮换后，旧 refresh token 已撤销；若新 token 本地保存失败，继续把旧磁盘文件标记为有效会让下次自动登录必然重放失效 token。logout 若磁盘清理失败又因调用者取消跳过远端撤销，则旧文件甚至仍可能包含有效 token。
- **决策：** 交互登录或启动 Restore 成功取得响应后，认证入口以不受该已完成 HTTP 调用者取消影响的提交边界保存新 refresh token，再返回会话。保存失败时尽力删除旧正式凭据，内存会话仍发布同一已验证响应的 access/refresh token，但 `IsCredentialPersisted=false`；不得回退旧 token、部分发布或自动重发认证写请求。
- **决策：** 启动 Restore 只读取一个凭据并发一次 refresh，响应 user ID 必须等于持久 user ID；401、身份错配或无法形成可信轮换结果的 2xx 响应同时清内存和凭据。附加 store 的会话在后续 rotation 中先尝试保存新 refresh token，再原子发布内存响应；成功响应后的本地保存不受 Dispose 取消，保存失败清旧凭据并标记未持久化。持久认证入口在旧会话完成 Dispose 前拒绝新 Login/Restore，避免单一凭据文件被不同账户会话交叉覆盖。logout 先清内存与凭据；若凭据清除失败，即使调用者取消也仍以会话生命周期尝试一次远端 revoke，并返回 `CredentialClearFailed`，不重试。
- **理由：** 服务端 rotation 是唯一权威提交点，客户端无法把网络与 DPAPI 合成分布式事务。保留当前已验证内存会话维持可用性，同时删除已知失效磁盘 token并显式降级持久状态；logout 的条件性不可取消 revoke 缩小“有效 token 留在不可删除文件中”的窗口。
- **影响：** Client 增加串行持久认证入口、内部 Restore HTTP 和会话 credential persistence 状态，扩展客户端 logout 状态；不改变 Shared/服务端协议、DPAPI 格式或依赖。进程在服务端轮换成功但收到响应前终止仍只能在下次 401 时清理旧 token，这是单次使用 rotation 的固有限制。
- **后续收窄：** `DEC-032` 追加无敏感信息的持久 clear barrier，使文件级占用导致正式凭据暂时无法删除时，后续进程仍拒绝自动恢复；认证目录整体不可写且远端 revoke 同时失败仍保留本决策的 `CredentialClearFailed` 降级边界。
- **来源：** `DEC-006`、`DEC-022`、`DEC-023`；工程落地方案第 9.3、12.5、18.1；`docs/ai/tasks/2026-08-03-stage-6-client-session-restore.md`；当前 ClientAuthenticationSession 与 ClientCredentialStore 实现。

### DEC-025：单账户 runtime 的启动、重连与终止所有权

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** 客户端已有认证会话、稳定 `AccountScopeIdentity`、fail-closed 本地缓存、Realtime FIFO sink 和账户内 Sync single-flight，但尚无单一所有者保证这些组件使用同一服务器/用户作用域。若先同步后连接 SignalR，会在同步结束到连接建立之间留下消息空窗；若切换账户时先释放认证会话或缓存，旧 Realtime/Sync 仍可能迟到写入或错误复用单一持久凭据。
- **决策：** 一个 `ClientAccountRuntime` 只拥有一个已认证 session 及由其 canonical server URI/user ID 派生的一个 AccountScope，并独占该 scope 的 cache、Sync coordinator 与 Realtime connection。并发 Start 共享一次启动；调用者取消只取消等待。启动先尝试建立 Realtime，再执行一次 `Startup` 权威同步；初始 Realtime 失败不得跳过可独立成功的 HTTP 同步，也不在运行时隐藏无限重连。
- **决策：** 自动重连从 `Reconnecting` 回到 `Connected` 时异步触发一次 `Reconnect` Sync；未知会话的 Realtime 消息沿同一请求器触发权威对账，不能阻塞 FIFO 事件分发。显式窗口激活、周期和 Realtime 重试只复用既有 Sync single-flight；Realtime retry 失败只记录异常类型并仍尝试独立的 HTTP Reconnect Sync。终止后拒绝新触发。
- **决策：** 账户切换或应用停止按 Realtime Dispose → Sync Dispose/取消并等待 → 启动与已登记显式 flight 收敛 → cache Dispose → session Dispose 的顺序执行；显式 sync/retry 必须在状态门内登记，使检查通过的操作要么完成/取消，要么被终止链等待。显式 logout 在 cache 收口后调用 session logout，再 Dispose session。普通 Dispose 保留 DPAPI 凭据，logout 清除凭据并远端撤销。session 最后释放，使持久认证入口在旧账户全部作用域工作停止前继续拒绝新 Login/Restore；factory 只有成功返回 runtime 后才取得 session 所有权，构造失败时 session 仍归调用方释放。
- **理由：** 先连接再补拉以 Realtime 捕获同步窗口内的新提交，未知会话仍 fail-closed 并请求权威对账；终止顺序先关闭所有生产者和共享循环，再关闭存储与认证，避免 use-after-dispose、跨账户迟到写入和凭据所有者重叠。
- **影响：** Client 增加内部账户 runtime/factory、轻量组件生命周期接口和组合 sink；现有 Realtime、Sync、cache、认证协议与存储语义不变。周期 timer、窗口/UI 状态、通知策略、Toast、托盘和多账户凭据历史仍属后续切片。
- **来源：** 工程落地方案第 9.3、12.5、12.7、阶段 6；`DEC-016`、`DEC-017`、`DEC-018`、`DEC-021`、`DEC-024`；`docs/ai/tasks/2026-08-03-stage-6-account-runtime.md`；当前 ClientRealtimeConnection、ClientSyncCoordinator、AccountScopedLocalCache 与 PersistentClientAuthentication 实现。

### DEC-026：本地未读派生、权威覆盖边界与 read-through 安全水位

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-020/021/025` 已把 Complete 会话列表、Sync 页和单账户 runtime 串起，但本地消息入口尚未携带来源或前台会话，`IsRead`、`IsNotificationHandled`、`UnreadCount` 与 `PendingReadThroughMessageId` 也没有同一事务语义。权威会话列表与消息页独立获取；列表响应形成后可先到达更高 ID 的 Realtime。若用可被本地到达推进的 `LastMessageId` 判断服务端 `UnreadCount` 是否已覆盖某条消息，会在列表落后、乱序到达或 Sync 回填空洞时少计；若前台 Realtime 直接把连续已读边界推进到尚未提交的全局 Sync cursor 之外，后续 read-through 上报会让服务端永久过滤客户端尚未见过的私有消息。
- **决策：** `IncomingMessageSource` 固定为 Realtime=1、Sync=2、History=3、SendResponse=4。Realtime 与每个 Sync 页在进入存储前捕获一次不可变的前台会话 ID；只有窗口可见、未最小化、拥有前台焦点且已打开该会话时才成立。本人、已读边界内、History、SendResponse、pending 提升和当前前台消息在同一 SQLite 事务置 `IsNotificationHandled=true`；本人、pending、History、边界内和前台消息同时单调置 `IsRead=true`。只有他人、未读、非前台且首次 `Inserted` 的 Realtime/Sync 消息保持两个标志为 false 并返回稳定 ServerMessageId 候选；Duplicate 只允许 false→true 的观察型收敛，Conflict 零写入回滚。pending 只允许当前账户 sender，创建时即为已读/已处理。
- **决策：** 每次权威登记同时保存该实例当前会话的 `AuthoritativeLastMessageId` 内存边界；新 store 仍必须先完成权威登记，所以不需要新 schema 或跨重启恢复该边界。首次到达是否补增未读以该权威边界判断，不以可被 Realtime 推进的本地 `LastMessageId` 判断。快照更新后的 `UnreadCount` 定义为“服务端权威 UnreadCount 减去权威窗口内本地已知已读的他人行，再加权威 LastMessageId 之上的本地未读他人行”；两个 ID 区间互斥。这样列表落后、Realtime 乱序、Sync 回填和本地前台已读不会重复或吞掉计数。History 行直接已读/已处理；重复观察已有未读行，或首次插入仍处于权威覆盖窗口的未读行时只单调减一次；首次插入高于权威上界的 History 行不得用已被 Realtime 推进的本地预览边界扣减其他消息的未读。
- **决策：** 前台消息行可立即标记已读/已处理，但连续 `LastReadMessageId` 只能推进到 `MIN(本次已见目标, 事务开始前已提交 LastSyncCursor)`；`PendingReadThroughMessageId` 保存未裁剪的本地最大目标并保持 MAX 单调。未来 HTTP 上报必须从提交后的磁盘状态派生，并且每次实际请求目标严格使用 `MIN(PendingReadThroughMessageId, 已提交 LastSyncCursor)`；不得直接提交原始 pending 值。只有权威列表确认的服务端 `LastReadMessageId >= PendingReadThroughMessageId` 才清除 pending。前台批量置读只允许从未读数扣减 `(原 LastReadMessageId, 本次目标]` 内仍未读的他人行；已在连续边界下方但尚未刷新行标志的旧行不属于当前未读基线，不得再次扣减。Sync 页内同一前台会话的 read-through 合并为一次批量处理，且只更新仍未读或未处理的行；消息/标志/未读/预览/候选/游标仍在一个事务，损坏游标或整页失败均 fail-closed 且不泄漏任何副作用。
- **理由：** 独立权威覆盖边界区分“服务端快照已计入”与“本地已经见过”，区间派生把权威基线和列表后的本地增量精确相加；逐行单调标志让后续通知协调器能够在派发前重检。连续已读边界按已提交 cursor 钳制则不会把未同步空洞误报为已读，同时保留原始 pending 让后续轮次在 cursor 追上后继续幂等上报。
- **影响：** Shared 增加来源枚举；Client 增加活动快照、来源上下文、带候选的 merge/page 结果以及 Realtime/Sync/runtime 接线，不改服务端 DTO、SQLite schema、migration、依赖或 Windows API。当前只产生明确候选且不派发；下个切片在扫描历史 `IsNotificationHandled=false` 前必须以 durable 版本键完成旧缓存收养，随后实现 Round/Recovery gate、串行 NotificationCoordinator 和按已提交 cursor 钳制的 read-through uploader。WPF 必须完整上报可见性、最小化、焦点和打开会话变化，不能把一次旧前台快照长期复用。
- **来源：** 工程落地方案第 12.3、12.5–12.8、13.1–13.4；`DEC-003`、`DEC-020`、`DEC-021`、`DEC-025`；`docs/ai/tasks/2026-08-03-stage-6-local-unread.md`；真实 SQLite 列表/Realtime/Sync/History 交错、整页回滚、账户隔离、已读边界下旧行与 10,000 行前台页测试；Claude #33 challenge、#34 review 与 #35 窄复审中经 Codex 复算和本机测试确认的空洞、权威覆盖、写放大与前台重复扣减发现。

### DEC-027：会话真实消息 read-through、确认收敛与快照级退避

- **状态：** 已接受；局部替代 `DEC-026` 中“直接使用数值 `MIN(PendingReadThroughMessageId, LastSyncCursor)` 作为 HTTP 目标”和“只有权威列表可清除 pending”的后续实现前提
- **日期：** 2026-08-03
- **背景：** `LastSyncCursor` 是跨会话的全局消息水位，数值 `MIN(raw pending, cursor)` 可能是另一会话的消息 ID，直接上报会被服务端目标归属校验拒绝。另一方面，成功 `ConversationReadReceipt` 已是服务端在动态权限和目标归属事务内返回的权威单调确认；即使列表快照尚未刷新，它也足以确认不高于 receipt 的当前 pending。若忽略 receipt，只靠列表清理，会在重启和高频触发中反复发送已被服务端接受的目标。永久错误、网络失败和 SQLite busy 若每次 Sync 页触发都重试，也会形成账户级请求风暴。
- **决策：** 原始 `PendingReadThroughMessageId` 仍保存本地前台最大目标。实际请求目标必须是同一会话真实存在、`IsRead=true`、不高于原始 pending 与已提交 `LastSyncCursor` 的最高 ServerMessageId，并且 `(旧 LastReadMessageId, 候选]` 内没有本地已知未读空洞；没有这样的行就不发送。原始 pending 本身必须属于该会话的一条本地已读服务器消息。单行损坏只隔离该会话并脱敏记录，不得让其他会话或整个 AccountScope 进入 fatal；批次按原始 pending 会话行分页，内存 deny、durable revocation intent 与 tombstone 任一命中均不得返回目标。
- **决策：** 成功 receipt 必须匹配会话且 `LastReadMessageId >= requested target`。应用 receipt 的单一 SQLite 事务将已知消息单调置读/已处理、仅扣减旧连续边界之上的已知未读行、推进 `LastReadMessageId`，并在 `receipt >= 当前 raw pending` 时清除当前 pending；并发出现的更高 pending 不得被较小 receipt 清除。完整权威会话快照仍可在服务端 `LastReadMessageId >= 当前 pending` 时清除 pending。两条路径都是权威确认，均不能回退边界。
- **决策：** 每账户 coordinator 只有一个 flight，并发触发最多登记一次补跑；调用方取消只取消等待，Dispose/Logout 才取消共享生命周期。成功目标、永久错误抑制和瞬时失败退避只保存在内存，并绑定成功提交的权威快照 revision：永久错误按会话抑制，认证/网络/SQLite busy 按账户延后，下一次成功快照使其重新可试。稳定 `ConversationAccessRevoked` 403 走 durable revoke/purge，普通 403 不做破坏性清理。没有新增 schema，也不宣称跨进程 exactly-once；重启可按服务端 `MAX(old,target)` 幂等重发。
- **理由：** 会话内真实行与空洞检查同时满足服务端目标归属和“客户端尚未同步的私有消息不能被越过”；receipt 与快照双权威收敛减少无意义重发，同时以当前 pending 比较保护并发新目标。快照 revision 给错误状态一个可证明的失效边界，既避免每页紧循环，又允许下一轮权威对账后恢复。
- **影响：** Client Storage 增加有界 pending 批次与 receipt 事务，Sync 增加 read-through HTTP transport/coordinator，并在已提交 Sync 页和前台 Realtime 合并后触发；账户 runtime 按 Realtime → Sync → read-through → cache/session 顺序终止。不修改 Shared/Server 契约、SQLite schema、migration、依赖、Notification/WPF 或 Windows API。
- **来源：** `DEC-003`、`DEC-011`、`DEC-020`、`DEC-021`、`DEC-025`、`DEC-026`；工程落地方案第 12.3–12.8；`docs/ai/tasks/2026-08-03-stage-6-read-through-upload.md`；真实 SQLite 跨会话 cursor、空洞、撤权、损坏行、busy、102 会话分页、重启、取消、receipt 竞争和 runtime 接线测试；Claude #36 challenge、#37 固定候选 review 中经 Codex 复算并在 `8384e61` 修正的退避、撤权批次竞争、损坏行隔离与文档漂移发现。

### DEC-028：通知旧状态收养、显式候选与同步轮次 gate

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-026/027` 已让 Realtime 与 Sync 在消息事务提交后返回通知候选，但升级前缓存中的 `IsNotificationHandled=false` 无法证明仍应提醒；直接扫描会补弹历史消息。Realtime、Sync 与恢复若各自调用平台，又会在同步关闭边界丢失或双派发。权威会话列表此前也未把当前成员 `IsMuted` 投影到 `ConversationDto`，派发前无法可靠重检静音。平台提交与本地确认不是同一事务，且撤权可能来自实时事件、权威全集缺失或 read-through 稳定 `403`。
- **决策：** 每个账户 cache 在任何消息生产者启动前，以立即写事务检查 `LocalAppState.NotificationStateVersion`；首次升级把版本键之前的全部未处理行单调置为 handled，并在同一事务写版本 1。事务失败同时回滚数据和版本，已收养版本重启幂等，新版本产生的 false 不再被收养。`ConversationDto` 以向后兼容可选字段增加 `IsMuted=false`；Server 按当前成员状态投影，Public 无个人行时为 false，Client 权威快照写入 `LocalConversations.IsMuted`，实时/同步合并在同一事务直接抑制静音候选。
- **决策：** 单账户只有一个串行平台无关 `ClientNotificationCoordinator`。调用方只能提交明确 ServerMessageId；存储在平台调用前以短 SQLite 事务重检撤权、已读、本人、静音、当前前台、DND、系统禁用和 `None`。平台 `Accepted` 后以不可被调用方取消的事务置 handled；`TransientFailure` 保持 false；明确永久不可用、配置禁用或策略抑制置 true。`Summary` 的全部确认使用一个本地事务；平台接受后、本地确认前崩溃按 at-least-once 恢复，不宣称 exactly-once。未配置平台属于 `Unavailable`，仍先提交已读、静音或当前前台等明确抑制项，只有其余合格候选保持 false，不能把暂不可用伪装成用户禁用。
- **决策：** 每个 Sync round 用同一内存 gate 和单调 generation 原子切换 Realtime 的“加入本轮/关闭后即时派发”。候选保留首次来源；每次打开都必须在 `finally` 关闭。只有 Startup 或成功提交权威快照的后台 Reconnect/Periodic 可从头读取最多 200 条旧 false 作为 Recovery，不保存持久 cursor；处理后下一轮重新从低 ID 扫描，因此后续候选和迟到低 ID 均可到达。完整 Startup 发一个 Summary；WindowActivated 或前台后台轮只以 None 收敛本轮；后台按过滤后数量 Automatic；失败或取消轮都只即时处理首次来自 Realtime 的候选，Sync 候选留给后续 Recovery；失败轮已捕获的旧 Recovery 可在后台继续处理，取消轮不处理旧 Recovery。
- **决策：** 实时撤权、权威全集缺失、持久 tombstone 重启重放和 read-through 稳定撤权都在 deny/tombstone 边界后经同一串行协调器清除会话平台状态。每次 tombstone 同事务写 `NotificationClearPending/<conversationId>` 并清旧完成标记；只有平台返回 Accepted 或 PermanentlyUnavailable 后才删除 pending，并在 tombstone 仍存在时写 completed。后续权威对账只重试 pending 或缺完成标记的 legacy tombstone，不重放已确认历史 tombstone；清除失败不恢复访问，重新加入也不丢未确认清理。账户终止顺序为 Realtime → Sync → read-through → notification → cache/session，确保所有生产者停止且在途通知收敛后才释放存储。调用方取消只取消等待；只有账户终止取消协调器生命周期和平台在途调用。
- **理由：** 收养版本把“无法证明仍需提醒”的旧行与新真源分开；显式 ID 和短事务避免持有 SQLite gate 跨平台调用；generation gate 给同步关闭与 Realtime 分流一个线性化点；三态平台可区分临时未接入与用户明确禁用；全撤权来源的幂等清除缩小已失权内容留在系统通知中的窗口。
- **影响：** Shared 增加加法 `ConversationDto.IsMuted` 与 `NotificationPolicy`；Server 只补投影，无 migration。Client 增加无 schema 变化的收养版本、候选/Recovery API、平台抽象、串行协调器、round gate 及 runtime 所有权；不新增依赖，不接 Windows Toast/WPF、声音、闪烁、托盘、激活或静音写 API。下一切片必须在该抽象上实现真实 Windows 权限探针、稳定 Tag/Group 和 UI activity 事件，不得绕过协调器直接调用平台。
- **来源：** 工程落地方案第 12.3、12.5–12.8、13.1–13.7；`DEC-003`、`DEC-017`、`DEC-018`、`DEC-020`、`DEC-021`、`DEC-025`、`DEC-026`、`DEC-027`；`docs/ai/tasks/2026-08-03-stage-6-notification-coordinator.md`；真实 SQLite 收养/恢复/损坏行/撤权/重启/清理确认、fake platform 三态/取消/并发、100 轮 round gate 竞争、runtime/HTTP/Server DTO 测试；Claude #38 challenge 与 #39 独立 review 中由 Codex 复算并经本机验证的结论。

### DEC-029：unpackaged Windows 通知传输、恢复语义与原生调用边界

- **状态：** 已接受
- **日期：** 2026-08-03
- **背景：** `DEC-028` 已冻结平台无关的三态通知协调与 durable 撤权清理确认，但默认客户端仍没有 Windows 传输。unpackaged WPF 的 App Notification 注册、同步 `Show`、设置读取和进程激活都经过 Windows App Runtime；运行时缺失、原生调用挂起或提交超时后迟到成功都可能让账户终止卡死、把可恢复环境永久烧掉候选，或在重试后留下旧 Toast。Toast 身份和激活参数还必须与本地 `AccountScopeId` 真源逐字节一致。
- **决策：** Client 使用 framework-dependent unpackaged `Microsoft.WindowsAppSDK 2.3.1`，`WindowsPackageType=None`；编译目标为 `net10.0-windows10.0.19041.0`，同时把 `TargetPlatformMinVersion` 与 `SupportedOSPlatformVersion` 固定为官方支持下界 Windows 10 1809 `10.0.17763.0`。Windows App Runtime framework/Singleton/Main 是目标机前置条件，自动 bootstrap 可能在进入托管 `App` 前失败；安装器、干净机启动与发布探针在阶段 11 固化，当前不会把开发机已安装 runtime 冒充部署通过。bootstrap 成功后缺通知 COM、`IsSupported=false` 或设置探针异常为 `Unavailable/TransientFailure`，保留候选和清理 pending 供环境恢复后重试；Windows 明确返回非 Enabled 设置时属于用户/策略配置性禁用，候选可按 `PermanentlyUnavailable` 完成。任何未知 COM/平台异常仍为 transient。
- **决策：** 进程只共享一个惰性 `AppNotificationManager.Default` 适配器，默认 factory 构造不提前触发缺失 runtime。WPF 启动先订阅原生及应用 handler，再在专用 LongRunning 任务中有界执行 `Register()`，注册成功后读取当前 `AppInstance.GetActivatedEventArgs()` 以覆盖冷启动通知；窗口先可响应地显示，Dispatcher 只异步等待后台 host。退出采用显式异步 shutdown，先移除应用 handler，再在后台有界 `Unregister()`，绝不调用会影响同身份其他进程状态的 `UnregisterAll`。注册超时未决时禁止再次注册，直至迟到任务完成并清理，避免旧清理注销新注册。注册、注销和同步 `Show` 都不得占用线程池普通 worker、阻塞 WPF UI 或让账户终止无限等待；默认边界为 10 秒。
- **决策：** 原生 `Show` 超时或调用方取消后结果不确定时，全局关闭后续提交，等待原调用终态；若它迟到成功，先按精确 Tag+Group 删除，清理成功后才重开。同 Group 撤权在不确定清理收敛前必须返回 transient；若迟到清理终态失败，下一次提交前以同一 Tag+Group 自动重跑一次有界恢复，权威同 Group 移除也可恢复并重开提交。挂起的恢复移除保持单 flight，在原生任务终态前不反复创建线程。该机制最多隔离一个挂起原生提交或清理，不以并发重试累积线程或制造晚到 Toast。设置通过一秒 TTL 的进程内快照读取，原生探针在专用线程运行且单次同步等待最多 250 ms；消息热路径不逐条直接调用设置 RPC。同步移除也从专用 LongRunning 线程发起并有界等待，瞬态失败不得确认 durable 清理。
- **决策：** 平台每次提交、会话清理和 Summary 清理都显式携带规范 `AccountScopeId`；不在共享传输中保存可变“当前账户”。只有单账户协调器构造请求，操作参数不可变且在边界再次验证为 32 字节 SHA-256 的规范 Base64UrlNoPadding。PerMessage 与 Summary 的 Group/Tag 完全按 13.5 计算；撤权确认前同时删除会话 Group 和可能混入该会话内容的账户 Summary。Summary 只显示数量，不含会话名、发送者或正文；Toast 三天到期并设置 `ExpiresOnReboot=true`。
- **决策：** 激活参数采用版本 1 的严格判别联合参数串，使用 Windows App SDK builder 原生要求的分号 `;` 分隔：Message 只允许 `v/target/account/conversation/message`，Unread 只允许 `v/target/account`。解析拒绝重复/未知/缺失字段、非规范 percent encoding、非规范 GUID/十进制 ID/AccountScopeId、空值、`+`、错误分隔符和超过 2048 字符的输入；安装态测试必须从 production builder 的真实 `Payload` XML 提取 launch 属性并往返严格 codec。日志和对象格式化不含参数、账户、会话、消息、显示名或正文。当前切片只把已解析目标交给 WPF 激活入口并恢复空壳窗口；目标特定导航、当前账户/权限复核、重复激活幂等与 `RedirectActivationToAsync` 单实例转交必须在下一切片完成，在此之前不得宣称真实点击导航通过。
- **理由：** 显式不可变作用域避免共享 singleton 的隐藏账户串线；可恢复环境与用户明确禁用分开，既不会在缺 runtime 时永久丢通知，又不会违背用户关闭通知的选择。专用线程、有界等待和不确定提交清理把不可取消的同步 Windows API 收敛成协调器可处理的三态，同时稳定身份降低 at-least-once 窗口的可见重复。官方注册顺序和当前激活读取覆盖运行中及冷启动入口，而不提前混入下一切片的授权导航与多实例协调。
- **影响：** Client/Client.Tests 增加 Windows App SDK 与 Debug logger 依赖、Windows 原生适配器/host/严格身份和激活 codec，默认账户 factory 改用真实动态平台；不改 Shared/Server 契约、SQLite schema 或 migration。安装路径必须在阶段 11 保持稳定并验证 runtime 前置条件；下一切片必须完成单实例重定向、账户与权限 fail-closed 导航，之后才接声音、任务栏和托盘生命周期。
- **来源：** 工程落地方案第 12.7–12.8、13.1–13.7；`DEC-028`；`docs/ai/tasks/2026-08-03-stage-7-windows-notification-platform.md`；Microsoft WPF App Notifications、通知管理、现有项目接入 Windows App SDK、App lifecycle instancing 与 Windows App SDK versioning 官方文档（2026-08-03 访问）；本机 runtime 缺失 `0x80040154`、官方 x64 runtime 安装态 production builder payload/Register/Show/GetAll/Remove、WPF 启动退出与双进程 AppInstance 探针；Claude #40 challenge、#41 review 与 #42 窄复审中经 Codex 复算并用本机测试确认的恢复分类、Summary 清理、设置缓存、原生分隔符、注册就绪、不确定提交竞争、自恢复和挂起调用边界。

### DEC-030：固定 AppInstance 单实例、完整激活转交与授权路由

- **状态：** 已接受；替代 `DEC-029` 中由通知 host 读取当前 `AppInstance` activation 的所有权安排，保留其冷通知必须先 `Register()` 再读取当前参数的约束
- **日期：** 2026-08-04
- **背景：** `DEC-029` 已提供 unpackaged Windows 通知传输和严格 target，但尚无唯一进程入口。仅激活窗口会丢失完整通知目标；自建 `Mutex + Named Pipe` 又会复制 Windows App SDK 已提供的版本/用户隔离、activation 序列化与 redirect 确认。主实例在重定向确认期间退出、多个继任者竞争、Windows 冷 COM 启动必须先注册才能读到 `AppNotification`，以及旧主注销与新主注册交叠，都会形成零主实例、双主实例或点击丢失窗口。
- **决策：** 每个进程使用固定 `RelayCove.Client.Primary` key 调用 `AppInstance.FindOrRegisterForKey`，不另建 Mutex、命名管道或新依赖。`WindowsAppSdkInstanceProvider` 是当前 activation 的唯一读取组件，并在第一次选举时捕获一次原始 `AppActivationArguments`；后续重选复用同一对象。拥有 key 的 registration 立即订阅 WinRT `Activated` 并最多缓冲 64 项早到事件；次实例把原始 activation 完整传给 `RedirectActivationToAsync`，普通次实例在确认后退出且从不注册通知。
- **决策：** 每次 redirect 同时观察 10 秒有界确认与目标 PID 退出。确认、失败或目标先退出后都等待默认 1 秒再重选；若成为 current 则以原 activation 接管，若仍是同一 PID 则按确认结果退出，若 PID 改变则改向继任者，最多三次，超限 fail-closed。该协议保证最终只有一个可响应主进程，但旧主已处理、确认前退出并由次实例接管时仍是跨进程 at-least-once；5 秒内存去重不宣称跨进程 exactly-once。
- **决策：** 实机 Windows App SDK 2.3.1 冷 COM 命令行给出独立精确 token `----AppNotificationActivated:`，并另带 `-Embedding`。只有包含该精确 token 的进程在第一次当前 activation 读取前调用通知 `Register()`；若它选举失败，只移除本进程 handler/ready 状态并退出，不调用会破坏同身份持久注册的 native `Unregister()`。主实例优雅退出先停止 dispatcher/router，再收敛 native notification `Unregister()`，最后释放 AppInstance key；异常退出 fallback 也先 detach 通知回调再释放 key。
- **决策：** 当前、redirected 与运行中通知归一到 WPF Dispatcher 上的串行路由。普通 Launch 只恢复唯一窗口。通知 target 在无活动账户时只保留最后一个、默认两分钟；显式活动账户必须逐字匹配 `AccountScopeId`，authorizer 必须同时确认认证会话、当前 runtime scope、内存权威快照，以及 Message 会话访问状态为 `Ready`，其他全部拒绝。授权目标先恢复窗口，再以完整判别联合身份在 5 秒/64 项内只调用一次导航 sink；每次授权重复点击仍恢复窗口，拒绝、异常或导航失败不消费目标。实际账户 lease、聊天定位与未读总览由阶段 8 接线，在此之前 production App 无账户时一律 park/fail-closed，不得冒充真实导航通过。
- **理由：** 复用 AppInstance 保持 Windows 原生 activation 语义并减少自定义 IPC 攻击面；目标进程退出观察与有界继任者改向覆盖 shutdown handoff，通知注销先于 key 释放避免旧主拆掉新主注册。账户、认证和权威缓存三重门确保 Toast 只是一条不可信导航请求，不能凭陈旧 ID 展示已撤权内容。
- **影响：** Client/Client.Tests 增加 activation provider/host/dispatcher/router 与退出顺序协调，不改 Shared/Server 协议、SQLite schema/migration 或依赖。pending 两分钟、跨进程 at-least-once 和实际账户/UI 尚未接线是明确边界；阶段 8 必须在 runtime 已有可用权威快照后建立/更新账户 lease，并完成真实导航体验 Gate。
- **来源：** 工程落地方案第 12.7–12.8、13.5–13.7；`DEC-017`、`DEC-025`、`DEC-028`、`DEC-029`；`docs/ai/tasks/2026-08-04-stage-7-single-instance-activation.md`；最终 600 项自动化、activation 600/600 压力、30 轮每轮 10 竞争者的真实优雅交接、冷/运行中/交接后真实 `INotificationActivationCallback`、并发冷启动与强杀恢复探针；Claude #43/#44 只读挑战中经 Codex 复算并以本机实测确认的读取所有权、继任者交接、退出顺序、授权与已知边界。

### DEC-031：Toast 后单轮桌面提醒与托盘驻留生命周期

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-028/029/030` 已冻结串行通知候选、Windows Toast 传输和单实例激活，但默认 WPF 进程仍没有声音、任务栏闪烁或托盘驻留。PerMessage 同步轮次可接受多条 Toast，失败轮还可能先后派发 Realtime 与旧 Recovery；若声音放在每条 Toast 或每次 dispatch 内部，会越过工程方案“一轮最多一次”的边界。Windows App SDK Toast 默认音频也会在门禁外逐条播放。窗口普通关闭若直接进入既有 `Closed` 路径，则会注销通知并释放实例键，无法继续实时接收或由次实例恢复。
- **决策：** `ClientNotificationCoordinator` 只在平台返回 `Accepted` 后尝试进程级 attention，并在本地 handled 确认前执行；attention 异常只记录异常类型，不改变 Toast 结果或重试平台。每次独立 dispatch 默认拥有一个原子 attention gate，同一 Sync round 显式把同一个 gate 传给其所有 dispatch，因此首次 Accepted 后声音与闪烁合计最多一次。Windows App SDK builder 必须 `MuteAudio()`，以门禁后的 `MessageBeep` 作为唯一声音来源。
- **决策：** Windows attention 使用线程安全的当前主窗口 HWND/前台快照。`MessageBeep` 在 Accepted 后尽力执行；只有非前台且 HWND 非零时调用 `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)`，激活窗口、未来打开对应会话或退出时用 `FLASHW_STOP`。`FlashWindowEx` 返回的是调用前窗口激活状态，不是操作成功值；适配器不得因后台窗口返回 false 打虚假失败日志，完成 Start 调用后必须记录匹配 STOP 责任。异常日志不得包含账户、会话、消息、正文或显示名。
- **决策：** WPF 主实例在窗口首次可见前创建 Windows Forms `NotifyIcon`，不新增 NuGet，第一版使用系统 Application icon。tooltip 与禁用菜单项显示 `0..999+` 总未读和全部 `ConnectionState`，Open/双击只恢复既有唯一窗口，Exit 只接受一次。托盘可用时普通 `Closing` 取消关闭并 Hide，保留通知 host、未来账户 runtime 和 AppInstance key；托盘初始化失败时允许真实关闭，避免不可恢复的无窗口进程。显式 Exit 先释放托盘和闪烁，再复用通知 native `Unregister()` 完成后释放 AppInstance key 的顺序；Windows 注销/关机不得被 Close-to-tray 拦截。
- **理由：** 共享 gate 与 Toast 静音把用户可观察的声音/闪烁口径绑定到已提交同步轮次，而非平台 Toast 数；明确 STOP 所有权覆盖窗口激活之外的未来会话打开入口。先建立托盘再显示窗口封住 `Window.Show()` 可能泵入极早 WM_CLOSE 的竞态，显式退出继续服从 `DEC-030` 的单实例交接安全顺序。
- **影响：** Client 启用 SDK 自带 Windows Forms 支持，增加无外部依赖的 desktop attention/tray 适配器、状态格式化和 WPF 生命周期接线；不改 Shared/Server 协议、SQLite schema/migration 或包依赖。当前 production App 尚未构造真实账户 runtime/chat UI，托盘只显示 `0 / Disconnected`，真实总未读/连接更新、会话打开 STOP 与消息端到端声音/闪烁必须在阶段 8 接线并保持 `未验证`，不能用 fake 或原生适配器 smoke 冒充。窗口隐藏到托盘时没有任务栏按钮，`FLASHW_TRAY` 无用户可见效果；系统注销/关机只完成代码级顺序复核，未执行破坏当前交互会话的实机探针。
- **来源：** 工程落地方案第 13.4、13.6、13.7 与阶段 7；`DEC-028`、`DEC-029`、`DEC-030`；`docs/ai/tasks/2026-08-04-stage-7-desktop-attention-tray.md`；最终 Fast/Full 629 项、Client 418 项、桌面/通知定向 280/280、复审补丁定向 39/39、安装态静音 Toast Register/Show/GetAll/Remove、真实极早关闭隐藏/次实例同 HWND 恢复/托盘 Exit、原生 MessageBeep/FlashWindowEx Start/STOP 探针；Claude #45 challenge 中经 Codex 复算并修正的 per-round gate、Toast 默认音频和 FlashWindowEx 返回语义发现，以及 #46 固定检查点 `PASS` 后补齐的零句柄诊断、独立 gate/声音 false 测试、取消会话结束闭锁恢复和显式限制。

### DEC-032：Production 账户壳的单一所有权、授权 lease 与退出顺序

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-022/024/025/028/030/031` 已分别提供真实认证、持久恢复、单账户 runtime、通知协调、授权激活与桌面驻留，但 production WPF 进程尚未组合这些组件。若窗口直接持有 session/runtime，重复登录、恢复与注销会产生重叠所有者；若在权威缓存就绪前建立通知授权，旧 Toast 可越过 fail-closed 门；若退出先拆通知/实例键再停止账户工作，迟到回调和缓存写入会跨越进程交接边界。
- **决策：** 主实例选举成功后才创建一个 production composition：一个 30 秒超时的 `HttpClient`、当前用户 LocalAppData 下分离的 Authentication/Accounts 根、`PersistentClientAuthentication`、注入真实 desktop attention 的 `ClientAccountRuntimeFactory`，以及一个 `ClientAccountShellCoordinator`。协调器以串行操作门独占至多一个认证操作、session、runtime 和 activation lease；调用者取消只影响尚未完成所有权转移的当前操作，Dispose/退出取消共享生命周期并等待已取得的账户所有权收敛。创建失败的 session/runtime 仍由当前调用路径释放，成功转移后只有协调器终止链可释放。
- **决策：** 启动先自动 Restore，无凭据、无效凭据、认证失败、网络/协议/限流失败都回到可操作登录态；登录输入先做绝对 HTTP(S) URI、用户名/密码和设备/版本长度校验，密码从 `PasswordBox` 取出后立即清空。runtime Start 或 Retry 只有在 Startup Sync 已完成并确认权威缓存可用时才建立账户 activation lease；暂不可用时保持目标停放，后续成功 Retry 建 lease 并异步重放。Startup/Retry 返回 `AuthenticationRequired` 时先撤 lease，再由 runtime Logout 清凭据并回到认证失败登录态。
- **决策：** 最新窗口 activity 在账户尚未创建时缓冲，runtime 接管前后各重放一次；可见、最小化和前台状态进入现有通知/未读判定，当前会话在本切片固定为空。Start/Retry/Logout 的真实连接与同步结果同时更新账户壳和托盘；没有持续状态事件时不得把边界快照宣称为实时状态。系统通知注册失败只在壳中显示可见降级，不阻断账户，候选继续保持可恢复。通知授权 sink 一律经 WPF Dispatcher 异步排队，避免 pending replay 在账户操作门内同步泵窗口消息。
- **决策：** Logout 顺序固定为 activation lease → runtime Logout/Dispose → 登录态；显式应用退出先停止账户 composition，再停止 activation dispatcher/router、注销 native notification，最后释放 AppInstance key。`OnExit` 只作为不能异步等待时的进程退出 detach 后备。账户快照、presentation、composition、runtime 与 `AccountScopeIdentity` 的 `ToString()` 必须脱敏；日志只记录错误类型，不记录服务器、用户名、scope、密码、令牌或本地路径。
- **决策：** logout 本地清理使用三个固定文件语义：DPAPI 正式凭据 `relaycove-credential.v1.bin`、原子发布临时文件 `.bin.tmp`，以及只以“存在即待清理”为含义、仅写单字节且不含身份/服务器/token 的 `relaycove-credential.v1.clear-pending`。`ClearAsync` 必须先 durable 建立 barrier，再删除正式/临时凭据；`LoadAsync` 见 barrier 时不得解密或发起 refresh，而是继续尽力清文件，仅在两者都清除后移除 barrier 并返回无凭据。该触发源不同于 `DEC-023` 的未知篡改/校验失败，不改变后者“不自动删除损坏正式文件”的边界。
- **决策：** 新 login/refresh 保存必须先原子发布新 DPAPI 凭据，后删除 barrier；两步之间崩溃会在下次启动丢弃一份有效新凭据并要求重新登录，但保持 fail-closed。若反序，崩溃可能让已注销旧 token 越过 barrier，因此禁止。barrier 写入与凭据删除若因认证目录整体只读、ACL deny 或不可用而同时失败，文件系统层无法再提供更强保证；必须返回并显示 `CredentialClearFailed`、记录脱敏 `ClearBarrierWrite` 类型，同时仍尽力远端 revoke。该残留与真实 ACL/只读卷组合保持 `未验证`，不得宣称绝对清除。
- **理由：** 单一协调器把认证与账户资源的所有权转移、授权建立和终止顺序收敛在可测试边界；权威缓存就绪门延续私有数据 fail-closed 语义，异步 UI sink 避免 WPF 消息泵重入。可见通知降级和明确边界快照让最小壳如实呈现当前能力，不以占位数据冒充聊天已接通。
- **影响：** Client 增加 production account composition/coordinator/presenter、最小登录/账户 WPF 壳与无 schema 的凭据 clear barrier。barrier 使 `LoadAsync` 在显式待清理状态下可删除正式文件；保存发布后、barrier 删除前崩溃会牺牲一次持久登录以换取 fail-closed。不改 Shared/Server 协议、DPAPI payload、SQLite schema/migration 或依赖。总未读仍显式为 `0（未接线）`，通知导航只进入受控占位入口；真实会话列表、持续连接/总未读、消息列表/发送、周期 Sync、真实服务器双客户端登录恢复、隐藏托盘闪烁和系统注销/关机实机仍属后续切片或 M5 Gate。
- **来源：** 工程落地方案第 9.2–9.4、12.5–12.8、阶段 8；`DEC-022`、`DEC-023`、`DEC-024`、`DEC-025`、`DEC-028`、`DEC-030`、`DEC-031`；`docs/ai/tasks/2026-08-04-stage-8-production-account-shell.md`；账户壳状态机/并发/取消/授权/activity/脱敏自动化、跨 store 凭据 barrier/文件锁/双失败回归、真实 Release WPF 登录/失败/托盘/单实例 smoke、SQLite 隔离重复回归与 Claude #47–#50 只读 challenge/review 中经 Codex 复算的发现。

### DEC-033：权威门控会话列表与版本化持续发布

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-017/020/025/026/032` 已提供账户隔离缓存、Complete 权威对账、单账户 runtime、未读派生和 production 账户壳，但 UI 没有安全的会话列表读取或持续状态源。直接读取磁盘会泄露尚未由当前进程权威确认的旧行；把缓存回调直接绑定 WPF 会让 SQLite gate、SignalR FIFO 和 UI Dispatcher 相互阻塞；注销/换号期间的迟到读取或事件还可能以更新的 UI 投递覆盖新账户。
- **决策：** 会话列表只在当前 `AccountScopedLocalCache` 已提交本轮 `Complete=true` 权威快照且 scope 非 fatal 时读取。读取与既有写入共享 operation gate 和 deferred 事务；SQL 同时排除撤权 tombstone 与 durable intent，最后消息必须以 `ConversationId + LastMessageId` 精确连接，读循环再应用当前内存 allow/deny 集。列表按 `UpdatedAt DESC, Id ASC` 确定排序；损坏行单独排除且不得计入总未读，busy 返回 transient，其他未知读取错误令 scope fatal。成功结果以不可变只读视图返回，真实总未读使用 `long` 中间值并饱和到 `int.MaxValue`，静音只影响提醒而不抹去未读。
- **决策：** 只有既有状态操作完成数据库提交并释放 operation gate 后才发布无 payload 的单调变更信号；fatal 只在首次转换时异步发信号。production runtime 用可停止 state hub 转发连接/会话状态；账户 coordinator 在当前 runtime subscription 内用 dirty single-flight 合流重读，并在发布前后核对 subscription、runtime 和 dispose 所有权。logout、认证失效、切换和 Dispose 固定先置空所有权并退订，再终止 runtime；列表和账户壳各自分配单调 revision，WPF 在 Dispatcher 上丢弃低 revision，handler 在锁外调用且异常隔离。
- **决策：** App 的托盘状态只消费同一账户壳快照中的持续连接与真实总未读，不从独立回调拼接。主窗口完整替换不可变列表后按 ID 恢复选择；通知待选目标只有在当前授权列表出现时选择，Ready 快照确认缺失时必须过期并回退用户原选择，账户进入非活动 phase 时清空，不能只凭跨 scope 可复用的 conversation GUID 导航。会话消息尚未真实渲染前，选择只改变 UI 高亮与空详情，`ClientActivitySnapshot.OpenConversationId` 保持 `null`，不得提前标已读或上传 read-through。
- **理由：** 权威门、精确 join 和双层撤权过滤把列表可见性绑定到当前账户真实授权；提交后轻量信号与单飞重读避免把存储锁或消息 payload 带入 UI。运行时引用校验解决“这是哪个账户”的安全问题，revision 只解决同一发布流的迟到顺序，两者不可互相替代。选择与已读分离避免下一消息切片落地前产生不可逆的未读丢失。
- **影响：** Client 增加无 schema 的会话列表 facade、runtime 状态 hub、版本化 coordinator 快照和虚拟化 WPF 双栏列表；不改 Shared/Server 协议、SQLite schema/migration、DPAPI、通知编码或依赖。消息 History/Around、真实消息视图、发送与会话打开后的 read-through 留在下一独立切片；真实服务器双客户端、通知点击与托盘数字视觉仍属于后续 UI/M5 Gate。
- **来源：** 工程落地方案第 9.2–9.4、12.5–12.8、阶段 8；`DEC-017`、`DEC-020`、`DEC-025`、`DEC-026`、`DEC-028`、`DEC-032`；`docs/ai/tasks/2026-08-04-stage-8-conversation-list-shell.md`；最终 Fast/Full 673 项、定向 410/410、完整 Client 2,310 次、真实 Release WPF 进程/单实例 smoke、model drift 与八项目漏洞审计；Claude #51–#53 只读第二意见中经 Codex 复算并本机验证的退订、版本、损坏行、选择过期与 live region 发现。

### DEC-034：有界消息窗口、原子历史合并与已应用视口读穿

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-026/027/033` 已提供本地未读、read-through、权威会话列表和版本化 UI 发布，但 production UI 尚不能有界读取消息、加载 History/Around 或证明某条消息已实际渲染。直接复用全量读取会让历史无界进入 UI；把“已选择”或“已发布”当作“已看到”会不可逆推进已读；History 先到、Realtime 先到、撤权和 Dispatcher 排队还会造成局部提交、旧快照复活或未应用消息被误读。
- **决策：** cache 在账户 scope/权威 allow/deny gate 内以 deferred 只读事务提供最多 50 条的排除式 keyset 页面，结果严格升序、去重且真正只读；busy 为 transient，损坏、fatal、未权威和撤权均 fail-closed。History/Around 传输严格校验 URI、会话/目标归属、顺序、数量、边界和 has-more 组合；整个页面通过单一 SQLite 事务复用唯一 merge 裁决，任一冲突或错误整页回滚。History 不推进 Sync cursor、不更新会话预览，也不产生通知候选或声音；首次拉到且尚未渲染的他人新行保持未读，只有既有已读边界内行保持已读。稳定 `ConversationAccessRevoked` 收敛到既有 tombstone/通知清理。
- **决策：** 每个当前选择独占 generation、取消源和方向 single-flight/dirty 合流；发布前核对 selection、runtime subscription 与 dispose 所有权。非 `Ready` 快照必须隐藏全部消息、分页标志和目标，并立即清除 rendered activity；旧账户、旧会话、迟到读取和撤权不能恢复视图。WPF 仅在 Dispatcher 应用不可变快照，启用 recycling virtualization；相同窗口重发不替换等价 `ItemsSource` 并保留 offset，旧页前插按 extent 差补偿，Realtime 只在用户位于最新区域时跟随，否则显示新消息提示。
- **决策：** Dispatcher 应用回执与后续视口滚动回执分离。应用回执只有仍为当前 `Ready` 发布 revision 且目标消息属于当前选择时才登记 `AppliedRevision`；滚动回执还必须等于该已应用 revision，不能读取 coordinator 中尚未应用的新快照。只有当前窗口前台、当前已应用视口位于最新区域时才设置 `OpenConversationId` 并单调提交精确渲染边界；离开最新区域、非 Ready、切换、注销或撤权立即清除 activity。写入 transient 不紧循环，认证失效结束账户会话。
- **理由：** 有界页面和原子 merge 把网络分页、SQLite 状态与 UI 可见性放在同一可证明边界；把 published、applied 和 viewport 三种状态分开，防止 Dispatcher 排队或无关状态重发把不可见消息误判为已读。非 Ready 空快照与 activity 清除延续私有数据 fail-closed 约束。
- **影响：** Client 增加无 schema 的有界 cache 页面、History/Around HTTP coordinator、消息选择状态机、虚拟化 WPF 列表和渲染后回执；不改 Shared/Server 协议、SQLite schema/migration、通知编码或依赖。当前只显示服务端已确认消息；输入、Text 发送、pending/失败重试、回复、搜索、附件和周期 Sync 留给后续切片。真实账户、VPS、第二客户端、通知点击后的端到端定位和 Narrator 仍属后续 UI/M5 Gate。
- **来源：** 工程落地方案第 9.2–9.4、10.4、12.3、12.6–12.8、阶段 8；`DEC-017`、`DEC-020`、`DEC-026`、`DEC-027`、`DEC-030`、`DEC-033`；`docs/ai/tasks/2026-08-04-stage-8-message-list-shell.md`；最终 Fast/Full 704 项、Client 493 项、关键集 810/810、真实 Release WPF 窗口/单实例 smoke、model drift 与八项目漏洞审计；Claude #54 完成挑战及 #56 额度失败前部分意见中经 Codex 独立复算并本机验证的 History 未读、渲染边界、协议校验、fail-closed 可见性、已应用 revision 与滚动保持发现。

### DEC-035：Durable pending、单次幂等写请求与显式原键重试

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-010/014/017/026/027/034` 已冻结服务端 INSERT-first 幂等发送、Realtime 回声、账户隔离缓存、未读/通知来源和有界消息视图，但客户端仍没有安全的写请求所有权。若先 POST 再落盘，进程崩溃会丢失幂等键；若对 timeout/429/5xx 自动重放写请求，会把“服务端已提交但响应丢失”的不确定状态变成隐式重复网络写；若 pending 伪造服务端 ID 或无条件失败覆盖，响应、Realtime、Sync 与取消竞争会产生双行或把 Sent 降级。
- **决策：** 当前切片只发送 `MessageType.Text`，逐 Unicode scalar 复用服务端 1–4000、非全空白、只允许 TAB/CR/LF 控制字符的精确口径，并保留合法首尾空白和换行。Reply、Attachment、Mention 均固定为空。每条新消息先在账户/会话权威门内以新 `ClientMessageId` 原子插入 `ServerMessageId=NULL, Sending`；每会话当前账户最多 50 条 outstanding。最新页面把确认消息和最多 50 条 pending/failed 分开读取，presentation 使用 nullable `ServerMessageId + ClientMessageId`，不得把 LocalId 或负数冒充服务端身份。
- **决策：** 写传输只对认证 401 做一次 refresh 后重放；网络、timeout、429、5xx 和取消都不自动重发 POST，而是条件性 `Sending -> Failed`。用户显式重试先原子 `Failed -> Sending`，复用数据库中完全相同的 ClientMessageId、ConversationId、Type、Content、ReplyTo 和 Mention；同一键同时重试共享一个 flight。201 Created 与 200 replay 都必须严格校验当前发送者及不可变请求字段，再以 `SendResponse` 进入既有统一 merge。响应、Realtime 或 Sync 谁先确认都只允许 `PendingPromoted/Duplicate`；`Sent` 永不被迟到失败降级，冲突保持失败并报协议错误。
- **决策：** 只有进程内该 scope 的第一次 cache 初始化把崩溃遗留 Sending 恢复为 Failed；同进程第二个 cache 实例不得误伤活动 flight。稳定 `ConversationAccessRevoked` 进入既有 durable purge/通知清理，认证失效结束当前账户；账户终止先取消并等待发送 coordinator，再释放 cache/session。会话选择切换不使用 selection token 取消已提交发送，但 shell 的迟到结果不得发布到新选择或新 runtime。
- **决策：** WPF 仅在当前 Ready 会话启用输入；Enter 发送、Ctrl+Enter 插入换行，失败行显式显示重试。当前实现等待发送调用返回后，只有 `PendingCommitted=true` 且输入仍逐字相同时清空，并在等待期间阻止同一输入重复提交；这牺牲慢 POST 时的即时清空，以确保未确认落盘前绝不丢输入。若未来要改成“落盘即清空、网络后台继续”，必须另行冻结 completion/authentication 事件所有权。
- **理由：** durable-first 把客户端崩溃恢复与服务端幂等键连接为同一个可证明状态；不自动重发不确定 POST 避免隐藏写放大，显式原键重试让 200 replay 安全收敛。条件化状态转换、严格响应发送者和统一 merge 覆盖响应/回声/同步/终止竞态，nullable 服务端身份避免 UI 把尚未确认的本地事实冒充服务器事实。
- **影响：** Client 增加无 schema 变化的 pending mutation/read/recovery、发送 transport/coordinator、runtime/shell 接线和 WPF 输入/重试；不改 Shared/Server 协议、SQLite schema/migration、通知编码或依赖。Reply、@、附件、草稿持久化、编辑/撤回、搜索和周期 Sync 仍留给后续切片；真实账户/VPS/双客户端、网络断连视觉和 Narrator 留到 M5 Gate。
- **来源：** 工程落地方案第 4.2、9.2–9.4、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-010`、`DEC-014`、`DEC-017`、`DEC-025`、`DEC-026`、`DEC-027`、`DEC-034`；`docs/ai/tasks/2026-08-04-stage-8-text-send-flow.md`；最终 Fast/Full 743 项、Client 532 项、关键集 250/250、真实 Release WPF 窗口句柄/响应/单实例 smoke、model drift 与八项目漏洞审计。Claude #57–#60 均因认证/额度或空闲调度无结论；Codex 固定差异自审发现并修正发送者响应校验与同进程恢复竞态后由本机自动化复验。

### DEC-036：账户级窗口激活与五分钟周期 Sync 调度

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-014/016/021/025/028/032` 已提供尽力投递的 SignalR、重连 Sync、账户 single-flight、通知轮次和 production runtime，但 production 只接通了 Startup/Retry/Realtime 的同步原因。工程方案明确要求 Periodic 补偿推送失败，并把 WindowActivated 定义为独立原因；若 WPF 直接创建 timer 或另跑同步循环，窗口事件噪声、托盘驻留、长同步与账户切换会产生重复请求、旧 scope 迟到触发或绕开现有通知策略。
- **决策：** 每个 `ClientAccountRuntime` 独占一个 `ClientAutomaticSyncScheduler`，调度器只向既有 `ClientSyncCoordinator` 请求原因，不实现网络、游标、重试、通知或第二同步循环。Startup Sync 完成后才启动调度；启动时当前 `IsMainWindowForeground` 只建立基线，之后仅 `false -> true` 上升沿请求一次 `WindowActivated`，重复 Activated/StateChanged/VisibilityChanged 不重复。账户创建期间的最新 activity 仍由 shell 在 runtime 接管后重放，因此启动中发生且最终仍有效的前台变化可形成上升沿。
- **决策：** Periodic 默认间隔固定为 5 分钟，首次从 runtime 启动完成后计时；每次自动请求观察结束后才等待下一间隔。长同步期间不积累 timer tick，若 tick 与其他原因重叠则继续由 `DEC-021` 的 single-flight、pending mask 和既有优先级裁决。同步返回失败或抛出异常只记录原因/状态/异常类型，下一周期继续；时钟本身异常记录后停止，避免故障 delay 形成热循环。间隔等待可在测试中注入，不新增配置、服务端状态或依赖。
- **决策：** 调度器拥有自己的取消源和被观察任务集合，但不拥有 Sync coordinator。runtime 注销、切换或退出先取消并等待调度 delay/观察者，再释放 realtime、Sync coordinator、cache 与 session；观察者取消只停止等待，不取消共享 Sync flight，随后 coordinator Dispose 按既有账户生命周期取消真正循环。停止后 activity 更新 fail-closed，不能向旧账户请求新同步。
- **理由：** runtime 是账户资源和托盘存活期的现有所有者，把自动触发放在此处可覆盖窗口隐藏期间的周期补偿并沿用统一终止顺序。上升沿过滤吸收 WPF 多事件噪声；完成后再计时形成天然背压，避免在网络长故障时积压任务。五分钟是在规范未给数值时兼顾聊天收敛延迟与小型服务端轮询负载的明确假设，未来若要外部配置必须另行冻结校验与部署口径。
- **影响：** Client 新增无依赖、无 schema 的自动 Sync 调度器并接入 runtime factory/activity/termination；不改 Shared/Server 协议、Sync 原因数值/优先级、HTTP retry、通知策略、DPAPI 或 WPF 布局。真实丢推送/VPS/双客户端与五分钟壁钟端到端行为留到 M5；本切片以注入时钟和真实 Windows 进程 smoke 验证本地调度与生命周期。
- **来源：** 工程落地方案第 4.2、9.2–9.4、12.4–12.8、21.2、阶段 8；`DEC-014`、`DEC-016`、`DEC-021`、`DEC-025`、`DEC-028`、`DEC-032`；`docs/ai/tasks/2026-08-04-stage-8-sync-triggers.md`；最终 Fast/Full 751 项、Client 540 项、关键集 670/670、真实 Release WPF 响应窗口/单实例/清理、model drift 与八项目漏洞审计。Claude #61 因认证源优先级失败，无结论；Codex 固定差异和本机门禁为最终依据。

### DEC-037：当前确认消息 Reply、durable 原目标与版本化输入上下文

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-010/012/017/034/035` 已提供服务端同会话 Reply 校验、Around、账户隔离消息窗口和 durable Text 发送。Shared、Server、SQLite 与 merge/retry 已保存 `ReplyToMessageId`，但新发送固定为空，Client HTTP transport 还拒绝所有非空 Reply；WPF 没有引用展示或输入上下文。若 UI 接受 pending、本地 ID、旧选择或缺失行作为新目标，会把未确认或陈旧事实带入写请求；若迟到发送结果只比较当前文字和值，则用户切走再切回或重选同一目标后仍可能被旧结果清空。
- **决策：** 新 Reply 只接受当前账户当前 `Ready` message selection 字典中仍存在的正 `ServerMessageId`；pending 行没有服务器身份且永远不可成为新目标。shell 在同一选择锁内验证并捕获会话与目标，选择切换不取消已经 durable 提交的 flight。send coordinator 在 HTTP 前把精确 nullable 正 `ReplyToMessageId` 写入 pending；transport 只拒绝非正 ID，201/200 响应继续逐字段匹配，显式 retry、Realtime 与 Sync promotion 均复用 durable 原值。Client incoming/pending 边界拒绝非正 Reply，避免畸形缓存进入可点击 UI。
- **决策：** presentation 只用当前已授权窗口中的确认消息解析引用发送者与正文。已加载目标显示真实摘要；缺失目标只显示“原消息未加载”并由用户点击后复用现有 `SelectConversation(conversationId, targetMessageId)` Around 路径，不为每行自动发请求。非 Ready、撤权、切换、退出均隐藏消息并清除 composer 引用；确认行可回复，pending 只展示自己 durable 保存的引用和失败/重试状态。presentation、pending、请求与快照 `ToString()` 继续隐藏正文、身份和 Reply ID。
- **决策：** composer 为会话 ID、Ready 状态和 Reply 操作维护单调上下文版本。发送捕获文字、会话、目标与版本；只有 `PendingCommitted=true` 且四者仍完全一致时才清空文字和引用。任何账户/会话/Ready 边界变化、选择或取消 Reply 都推进版本，因此旧 flight 即使后来观察到相同表面值也不能覆盖新 UI。`@用户` 不并入本切片：当前没有普通用户目录，Public 成员 API 也按冻结契约拒绝该用途。
- **理由：** 当前 selection 的确认 ID 门把 UI 意图绑定到已授权服务器事实；durable 原目标延续 `DEC-035` 的幂等恢复语义；缺失引用的显式 Around 同时避免请求风暴与伪造摘要。上下文版本解决单纯值比较无法区分 ABA 用户操作的问题。
- **影响：** Client 扩展无 schema 变化的 send/runtime/shell、transport/incoming 校验、presentation 和 WPF Reply 交互；不改 Shared/Server 协议、SQLite schema/migration、依赖、附件或 Mention。真实服务器/VPS、双客户端、Narrator 与 Reply 视觉端到端留到 M5 Gate；`@用户` 需先冻结普通用户目录/可选成员协议。
- **来源：** 工程落地方案第 4.2、9.2–9.4、10.4、12.1–12.3、21.2、阶段 8；`DEC-010`、`DEC-012`、`DEC-017`、`DEC-034`、`DEC-035`；`docs/ai/tasks/2026-08-04-stage-8-message-reply.md`；最终 Fast/两次 Full 760 项、Client 549 项、关键集 1,130/1,130、真实 Release WPF 响应窗口/单实例/清理、model drift 与八项目漏洞审计。Claude #63/#64 因认证源优先级失败，无结论；Codex 固定差异与本机门禁为依据。

### DEC-038：有界 HTTP(S) 识别、当前快照授权与参数化 shell 打开

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** 阶段 8 要求链接识别，但当前 Text 正文只按普通文本展示。若把整段正文交给 shell、拼接命令行、允许任意 scheme 或让陈旧 DataTemplate 直接启动，会把聊天输入升级为命令执行/外部协议入口，并可能在账户/会话切换后打开旧 scope 内容。正则回溯、无限链接控件和超长 URL 还会让 4000 scalar 的合法消息形成 UI/CPU 放大。
- **决策：** parser 以无 regex 的单趟 scheme 扫描和单趟括号平衡处理展示正文，只识别大小写不敏感的 `http://`/`https://`。每条消息最多保留首次出现的 8 个规范 URI，每项候选和规范结果均不超过 2048 字符；必须为绝对 URI、host 非空、user-info 为空，并拒绝反斜杠、空白/控制/Unicode Format 字符。常见中英文句末标点被剥离，只有未匹配的尾部闭括号被剥离；原正文和 Copy 内容不改变。
- **决策：** link presentation 同时保存原展示 token 与规范绝对 URI，但 `ToString()` 对两者整体脱敏。WPF 只渲染显式按钮，不自动联网、预览或打开；点击值必须仍存在于当前 `Ready` 不可变消息 snapshot。launcher 再执行同一 URI 校验，只构造 `UseShellExecute=true`、`FileName=规范绝对 URI`、空 `Arguments/ArgumentList/Verb/WorkingDirectory` 的 `ProcessStartInfo`，不经 cmd/PowerShell 或字符串命令。Win32/关联缺失等已知启动失败只显示不含 URL 的可恢复状态，未知异常不静默吞掉。
- **理由：** 双层 scheme/host/载荷校验与当前快照成员门把“不可信聊天文本”降为“当前授权用户显式点击的 URL association 请求”；不拼参数封住 shell metacharacter 注入。固定数量/长度和线性算法让 UI 成本受消息协议上限约束。localhost、IP、内网、query 和 fragment 在显式点击后允许，因为客户端不承担内容信誉或网络分类职责；未来若加入信誉服务必须另行冻结隐私和联网边界。
- **影响：** Client 增加无依赖、无 schema 的 link parser/presentation/current policy/shell launcher 与 WPF 按钮；不改 Shared/Server、消息正文、Copy、缓存、read-through、通知或日志。`file/mailto/ftp` 等外部协议、inline 富文本、网页预览、信誉检查、点击历史和自动打开明确不做；真实浏览器打开不由自动化触发，保留人工/M5 Gate。
- **来源：** 工程落地方案第 9.2–9.4、18.4、阶段 8；`DEC-034`、`DEC-037`；`docs/ai/tasks/2026-08-04-stage-8-safe-links.md`；最终 Fast/两次 Full 782 项、Client 571 项、parser/policy/launcher/presenter 关键集 190/190、真实 Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计、敏感日志检索与空白检查。Claude #65 因认证源优先级失败，无结论；Codex 威胁建模与固定差异自审发现并修正初版二次扫描后由本机门禁复验。

### DEC-039：selection 冻结未读边界与分页证明后的新消息分割线

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-026/027/034` 已冻结本地未读、渲染后 read-through 与有界 History/Around，但阶段 8 尚无“新消息”分割线。若 presentation 每次读取当前 `UnreadCount` 或 `LastReadMessageId`，首屏应用后立即发生的 read-through 会让标记移动或消失；若只在最新 50 条中选择首条大于边界的消息，未加载的更早页可能包含真正的第一条未读，UI 会显示一个看似精确但实际错误的位置。pending 无服务器身份，自己的确认消息也不构成未读。
- **决策：** `ReadMessagePage` 在既有账户/会话权威门内的同一个 deferred SQLite 事务同时读取非负 `LastReadMessageId`、`UnreadCount`、确认消息页和 pending；失败结果不携带可展示边界，outcome 的边界与消息 ID 在 `ToString()` 中脱敏。message selection 只在第一次成功最新页读取时冻结该状态；refresh、读穿、旧异步结果和后续页不得覆盖。零未读立即解析为空，非 `Ready`、切换、撤权和账户终止继续通过既有 selection generation 清空全部 presentation。
- **决策：** 有未读时只在连续分页事实证明精确位置后解析一次。最新 History 页必须已经无更早页，或其最老 ID 已不大于冻结已读边界；更早 History 页逐页满足同一条件后才解析。Around 页必须从会话起点或跨过冻结边界；若页内存在首条大于边界的他人消息即可精确落点，若没有且仍有更新侧缺口则保持未解析。解析后从有序确认消息中选择第一条 `Id > frozen LastReadMessageId` 且发送者不是当前用户的消息，冻结其服务器 ID；pending 和自己的消息永不显示分割线。宁可暂时不显示，也不显示近似位置。
- **决策：** WPF 在现有虚拟化消息行内、目标消息卡片之前显示唯一的可访问“新消息”分割线。该标记在 selection 生命周期内保持稳定，即使 read-through 已确认；离开再打开时使用新的原子本地状态重算。它不改变滚动策略、新消息提示按钮、Copy/Reply/链接、未读计数或 read-through 上传。
- **理由：** selection 冻结区分“用户打开时尚未读”与“当前数据库已读”，避免 UI 回执反过来抹掉自己的定位依据；分页证明把有界加载与精确 UX 连接起来，阻止 History/Around 缺口造成错误标记。复用现有事务、generation 和 presentation，不新增持久状态，也不会在重启后保留陈旧 UI 标记。
- **影响：** Client 扩展无 schema 的本地页 outcome、selection/presenter 和 WPF 行模板；不改 Shared/Server 协议、SQLite schema/migration、消息合并、通知或依赖。超出当前加载范围且尚未被分页证明的边界暂不显示；真实登录、VPS/双客户端与 Narrator 保留到 M5 Gate。
- **来源：** 工程落地方案第 9.2–9.4、12.3、12.6–12.8、阶段 8；`DEC-026`、`DEC-027`、`DEC-034`；`docs/ai/tasks/2026-08-04-stage-8-new-message-divider.md`；最终 Fast/两次 Full 788 项、Client 577 项、cache/shell/presenter 关键集 520/520、真实 Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计与空白检查。Claude #66 因认证源优先级失败，无结论；Codex 固定差异、自审与本机门禁为依据。

### DEC-040：会话作用域提及候选与发送授权同构查询

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** Shared、服务端消息存储和 `DEC-010/035` 已支持最多 20 个不可变 `MentionUserIds`，发送事务也会验证 Public 目标为活跃用户、Private/Direct 目标为活跃成员，但普通客户端没有安全获得这些 ID 的协议。复用管理员用户响应会泄露不必要的管理员/禁用等状态；复用 `/members` 会破坏 Public 稳定类型冲突契约；开放无界全局用户目录又会把聊天功能扩大成新的枚举面。
- **决策：** 新增认证的 `GET /api/conversations/{conversationId}/mention-candidates`，只接受 1–64 位 ASCII 用户名字符前缀和默认 20、范围 1–50 的 limit。响应只包含会话 ID、`UserId/UserName/DisplayName` 候选与 `HasMore`；候选和响应 `ToString()` 全量脱敏，不返回密码/哈希、管理员/禁用、在线时间、成员角色/已读或头像。用户名是唯一可插入 token，昵称只用于展示；本切片不提供昵称/模糊搜索或全局目录。
- **决策：** 候选 SQL 自身同时约束当前 actor 活跃、会话未删除且按消息访问规则可见、candidate 活跃，以及 Public 为任意活跃用户、Private/Direct 为当前成员；该规则与 `MessageCommandService.AreMentionsAccessibleAsync` 同构。规范用户名以 invariant-uppercase 匹配，`LIKE ... ESCAPE '\\'` 把合法 `_` 当作字面字符；按 `NormalizedUserName, UserId` 稳定排序，读取 `limit+1` 后截断并生成 `HasMore`。只有候选 SQL 返回零行时才执行同一可见性查询，区分授权空 200 与未知/删除/撤权 403；正结果不会先进行一个可与候选读取分离的宽松授权检查。
- **决策：** 允许返回当前用户，因为发送端“当前可访问正常用户”契约本身允许自提及；客户端可在 UX 层降低或隐藏，但不得制造服务端拒绝的伪规则。查询和用户名/昵称不进入应用日志或错误详情；actor/conversation ID 继续作为现有授权审计元数据。SQLite busy/locked 继续由统一错误中间件映射稳定 503。
- **理由：** 会话作用域 endpoint 同时覆盖 Public 和成员型会话而不改变 `/members`，且候选查询自身绑定授权，避免“先授权、后目录查询”之间把撤权后的数据当成当前结果。限制字段、前缀和结果量把必要的普通用户发现能力压缩到 `@用户` 的实际用途；发送事务仍是最终授权真源，可拒绝搜索后发生的禁用或撤权。
- **影响：** Shared 增加两个向后兼容响应 record，Server 增加无 schema 的 validator/query service/GET endpoint；不改数据库、迁移、现有成员/消息协议、发送验证、客户端或依赖。客户端候选传输、token 编辑语义和 durable 非空提及发送留在下一切片。
- **来源：** 工程落地方案第 8.3、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-009`、`DEC-010`、`DEC-035`；`docs/ai/tasks/2026-08-04-stage-8-mention-candidates.md`；最终 Fast/两次 Full 807 项（Shared 37、Server 192、Client 577、Updater 1）、Shared/validator/真实 HTTP/SQLite endpoint 关键集 190/190、model drift、八项目漏洞审计与空白检查。Claude #67 因认证源优先级失败，无结论；Codex 威胁建模、固定差异与本机门禁为依据。

### DEC-041：客户端提及以正文 token 存活条件冻结 durable ID 集合

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-010/035/040` 已冻结最多 20 个不可变提及 ID、可靠 pending/retry 和会话作用域最小候选，但客户端尚未定义候选迟到竞态、正文编辑后 ID 是否保留、服务端规范排序与本地不可变载荷的衔接，以及异步提交完成后的组合器清理条件。
- **决策：** 第一版候选只由用户在当前 Ready 会话显式提交 1–64 位规范用户名字符前缀查询；不随正文每次键入自动联网。候选 transport 只接受会话 ID 相等、字段有效、大小写不敏感前缀命中、唯一且按规范用户名/ID 稳定排序、数量不越过请求 limit、`HasMore` 与满页一致的响应。Shell 在请求前后验证同一 runtime、会话与 selection version；旧账户、切换、撤权或刷新后的迟到结果不发布。
- **决策：** picker 插入唯一身份 token `@UserName`，昵称只展示。内存候选 ID 只在正文仍存在大小写不敏感、前后用户名字符边界完整的相应 token 时保留；删除、改名或拼接为邮箱/更长用户名会移除 ID。会话切换、非 Ready 和账户结束清除候选/已选状态。最多保留 20 个唯一非空 ID，不把 ID 呈现或写入日志。
- **决策：** 发送入口在创建 pending 前校验提及集合并按 `Guid` 稳定排序，随后以该只读快照贯穿本地 mention 行、HTTP、SendResponse/Realtime merge 和显式 retry；retry 只能读取持久失败行的原集合。组合提交同时捕获正文、reply、排序 ID、会话与 context version，只有 pending 已提交且所有上下文仍相等时才清空正文/reply/mentions，防止迟到完成覆盖新编辑或新选择。
- **理由：** 正文 token 是用户可见意图，ID 是授权与不可变协议载荷；以严格 token 存活条件维护二者对应，既避免正文已删除却仍静默提及，也避免昵称歧义。pending 前规范排序使服务端 `Order()` 响应、Realtime 与本地顺序比较一致，并确保不确定结果的同键重试不会改变语义。
- **影响：** Client 增加无 schema 的候选 transport/coordinator、token policy、runtime/shell/WPF 接线并扩展发送方法参数；不改 Shared/Server 协议、SQLite schema/migration 或依赖。富文本高亮、自动补全、昵称/模糊/全局搜索与真实跨机体验不在本决策范围。
- **来源：** 工程落地方案第 8.3、10.4、12.1–12.3、12.6–12.8、阶段 8；`DEC-010`、`DEC-017`、`DEC-035`、`DEC-040`；`docs/ai/tasks/2026-08-04-stage-8-mention-compose.md`；最终 Fast/三次 Full 860 项（Shared 37、Server 192、Client 630、Updater 1）、提及/发送/shell 关键集 740/740、真实 Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计、敏感日志检索与空白检查。无登录会话下账户面板按设计折叠，因此 picker UIA 未冒充通过，真实登录视觉/键盘/Narrator 留到 M5。Claude #68 因认证源优先级失败，无结论；Codex 固定差异与本机门禁为最终依据。

### DEC-042：未绑定附件以有界 multipart 流进入非公开随机文件与事务元数据

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** Shared 协议已携带 `AttachmentDto` 与消息附件集合，但所有写入/投影仍冻结为空，服务端没有附件表、存储根或上传入口。阶段 9 第一条要求先上传再以 ID 发消息；因此上传成功到消息提交之间必然存在只属于上传者、尚无会话授权上下文的未绑定附件。直接使用请求文件名、`IFormFile` 无界缓冲或先提交 DB 再落盘会分别引入路径穿越/覆盖、资源耗尽或永久坏行。
- **决策：** `POST /api/attachments` 只接受正常认证 subject 的一个 `multipart/form-data` 文件 section，字段名固定 `file`，拒绝所有额外字段/文件。使用 `MultipartReader` 流式处理并分别限制总请求、boundary/header、section 和实际写入字节；默认单文件 25 MiB、硬上限 100 MiB，按 subject 固定窗口限流。超限返回稳定 413，且 exact-limit 必须可成功。
- **决策：** 原始文件名仅作有界展示元数据，验证后也绝不参与物理路径、日志、错误或 `ToString()`；声明 MIME 仅作规范化元数据。服务端在受信任配置根内生成无扩展、严格字符集、不可预测的暂存/最终 basename，以 `CreateNew` 和同目录无覆盖 rename 防止覆盖；根目录不经静态文件中间件公开，未绑定附件不提供下载。
- **决策：** 内容流入暂存文件时计算 SHA-256。暂存完成后开启 SQLite 写事务并再次确认 actor 正常，插入 nullable `MessageId` 元数据行，在事务提交前把暂存文件 rename 为最终名，随后 commit；任何正常异常/取消都回滚并清理本次文件。该顺序保证不会提交一个尚未存在物理文件的行，但进程在 rename 与 commit 之间崩溃可能留下无行文件，因此启动恢复只清理严格托管命名且数据库无对应行的暂存/最终残留，不删除未知文件。
- **理由：** 上传 endpoint 尚无会话信息，只有“上传者拥有的未绑定 reservation”是可证明的最小授权状态；真正加入消息时仍须在消息写事务内验证上传者、未绑定状态与会话权限。无扩展随机名、非静态根与不可下载状态把任意内容保持为不执行、不分发的 opaque blob；多层限长与 subject 限流约束单请求和短期滥用。
- **影响：** Server 新增 attachment schema/migration、options、流式 parser、文件存储/启动恢复、上传 endpoint 和限流；Shared 只补稳定过大错误码与 `AttachmentDto` 脱敏，不改变 JSON 字段。消息仍只支持 Text 且要求空附件，Client/schema/Sync/Realtime 不变。病毒扫描、内容嗅探、长期未绑定租约/配额、下载和 attach-once 消息事务必须在开放内容读取前继续决策。
- **来源：** 工程落地方案第 3.4、7.5、8.2、10.2、11.1–11.2、14.1–14.2、17.2、18.4、阶段 9、21.3；Microsoft ASP.NET Core 10 上传与限流官方文档（2026-08-04）；`docs/ai/tasks/2026-08-04-stage-9-attachment-upload.md`；最终 Fast/两次 Full 911 项（Shared 39、Server 241、Client 630、Updater 1）、Attachment Release 定向集 390/390、真实 Kestrel exact-limit 201/宿主级 413/不透明字节一致落盘、migration up/down、冲突回滚与启动恢复、model drift、八项目漏洞审计、敏感日志检索与空白检查。Claude #69 因认证源优先级失败，无结论；Codex 固定差异、威胁建模与本机门禁为最终依据。

### DEC-043：附件集合是消息幂等载荷并以条件更新 attach-once

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-010/042` 已分别冻结消息 INSERT-first 幂等和未绑定附件 reservation，但尚未连接二者。简单地先把附件设为某条消息再插入消息会在幂等冲突前产生副作用；只做“先查 MessageId 为空、随后普通 UPDATE”又允许两个并发消息基于旧读结果重绑同一附件。下载若只按 uploader 或附件 ID 返回，还会绕过当前会话撤权。
- **决策：** 普通发送端开放 Image/File，各携带 1–10 个唯一非空附件 ID；Text 仍严格为空，System 仍不开放。附件 ID 与 mentions 都是无序不可变 payload，按 GUID 规范排序。Image/File 正文可为 null，非 null 复用 Text 内容边界；Image 只接受声明为 `image/*` 的附件，但 MIME 仍不作为内容可信证明。
- **决策：** 同一 Serializable 写事务先按现有规则复核 actor、会话、Reply、mentions，并确认所有附件属于 actor 且物理文件完整；预检允许附件已绑定，以便相同幂等键 replay 到 INSERT 冲突后比较原集合。新消息必须先 INSERT，再以 `Id IN (...) AND UploaderUserId=actor AND MessageId IS NULL` 条件一次更新绑定，受影响行数必须精确等于规范集合；不相等即回滚新消息。目标幂等冲突只回读同发送者原消息及完整 attachments/mentions；完全相同返回 200，不同返回 409，均不重绑或发布。
- **决策：** Send/History/Around/Sync/SignalR 返回同一稳定排序 AttachmentDto 集合。metadata/download 授权查询必须把 attachment -> message -> 当前可见且未删除 conversation 绑定在一起；未知、未绑定、删除和不可访问统一 fail-closed。下载只打开严格托管路径，使用 attachment disposition、range、`nosniff` 与 `private, no-store`，不在日志/错误中暴露原名、物理名、路径或 hash。
- **决策：** 未绑定 reservation 默认 24 小时、最多配置 168 小时并至少每小时清理。清理先在 SQLite 写事务提交删除过期未绑定行，再尽力删除物理文件；失败或崩溃只留下无行文件，由严格托管 orphan recovery 后续回收。绑定行、未到期行和未知文件不删除。
- **理由：** INSERT 后的 owner/null 条件更新同时保持原幂等顺序和数据库裁决的 attach-once；允许已绑定预检使合法 replay 不会被“只能绑定一次”的新建规则误拒绝。授权查询从消息会话重新求值，确保撤权后的新下载请求不能借 uploader 或陈旧 DTO 绕过。DB-first TTL 删除把失败方向限制为可恢复孤儿文件，而不是不可恢复的“行存在但文件缺失”。
- **影响：** Server 扩展消息验证/命令/投影、附件查询/下载和后台 lease 清理，无新消息 ID、Sync cursor 或数据库列。现有 Client 明确拒绝非空附件 DTO，本切片不宣称客户端兼容；客户端发送/cache/UI、病毒扫描、搜索和 VPS 另开任务。
- **来源：** 工程落地方案第 7.4–7.5、8.2、10.2、11.1–11.2、12.1–12.4、14.1–14.3、阶段 9、21.2–21.3；`DEC-010`、`DEC-014`、`DEC-042`；`docs/ai/tasks/2026-08-04-stage-9-attachment-message-download.md`；固定代码提交 `41f7d11e207fd984bbc3e2a8c003f9bf2ed6a2e9` 最终 Fast/两次 Full 924 项、Attachment/Message Release 定向集 960/960、真实 Kestrel 的 201/200/409/403/200/206/401、lease DB/file/cancel 故障、model drift、八项目漏洞审计、日志脱敏与空白检查。Claude #70 因认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 固定差异审查与本机门禁为最终依据。

### DEC-044：客户端附件元数据随消息原子入库并限制为受信任相对路由

- **状态：** 已接受
- **日期：** 2026-08-04
- **背景：** `DEC-043` 已让所有服务端消息投影携带完整附件集合，但客户端 v1 本地缓存仍明确拒绝非空 `Attachments`，回读固定为空且不可变重复比较不包含附件。直接只改 DTO 接受会让重启后丢字段、同 ID 不同附件被误判重复；非原子建表或消息/附件分开提交又会产生半升级和不可展示消息。
- **决策：** 本地 schema v2 新增工程方案中的 `LocalAttachments`，`Id` 为主键，nullable `LocalMessageId` 外键对 `LocalMessages.LocalId` 级联并建立索引；远端展示元数据与 `DownloadUrl` 持久化，`LocalPath/ThumbnailLocalPath` 初始为空，`DownloadStatus=0`。本切片只创建绑定到已确认消息的行，不创建游离附件或访问物理内容。
- **决策：** 初始化只接受 `PRAGMA user_version` 0/1/2；所有 `CREATE IF NOT EXISTS`、`LocalAppState.SchemaVersion=2` 和 `user_version=2` 在一个 immediate SQLite 事务中提交，提交前故障必须完整回滚，未来版本不得降级。既有账户数据库原位升级，不改 `AccountScopeId` 或数据库路径。
- **决策：** Image/File 必须携带 1–10 个唯一且按 .NET Guid 规范排序的附件，Text/System 必须为空。每项只接受服务端冻结的安全展示文件名、规范小写无通配 media type、1–100 MiB、精确 `/api/attachments/{id:D}/download` 和空 `ThumbnailUrl`；因此远端字段不能把客户端引向任意主机，未来缩略图或 URL 形态变化必须显式升级协议。
- **决策：** 新消息行、mentions 和 attachments 在同一现有写事务插入；回读按 Guid 规范排序恢复完整 DTO。重复判定比较全部远端附件字段，但明确忽略未来本地可变路径/下载状态。现有客户端只产生 Text pending，带附件的响应不提升 pending。撤权仍先建立 deny-set/intent/tombstone，删除会话后由双层外键级联消息及附件；损坏本地附件行进入既有 fatal fail-closed。
- **理由：** 同事务和完整不可变比较把 Realtime/Sync/History/SendResponse 乱序统一到现有消息合并裁决；严格相对路由把当前仅存储的网络定位符限制在同一受信任服务端端点。nullable 外键与工程方案一致，可在后续上传切片复用，但当前不提前开放游离行行为。
- **影响：** Client 本地 schema 从 v1 升到 v2 并开始兼容服务端附件消息；Shared/Server、Sync cursor、消息 ID、账户 scope 与依赖不变。上传/发送、下载内容、物理缓存、缩略图、打开和 UI 留在后续切片。
- **来源：** 工程落地方案第 3.4、12.3、12.6–12.8、13.1、14、阶段 9、21.2–21.3；`DEC-017/018/026/034/043`；`docs/ai/tasks/2026-08-04-stage-9-client-attachment-ingestion.md`；production `53a5b63`、最终测试头 `722ad49`；最终 Fast/两次 Full 932 项、Client 附件定向 990/990、真实 SQLite 迁移/回滚/竞争/级联/隔离、所有协议入口、model drift、八项目漏洞审计、敏感日志与空白检查。Claude #71 认证失败；#72 实际 Opus 只读取证后被宿主中断且恢复连接失败，均无正式结论；Codex 固定差异与本机门禁为最终依据。

### DEC-045：非幂等客户端上传与 durable 附件消息以 pending 创建为恢复边界

- **状态：** 待验证
- **日期：** 2026-08-04
- **背景：** 服务端上传 endpoint 没有客户端幂等键，成功后先形成未绑定 lease；客户端现有 Text 可靠链则以本地 pending 和 `ClientMessageId` 为重试身份。若网络失败后自动重传上传会制造未知数量的 server orphan；若只在内存保存 201 DTO，上传成功到消息 POST 之间的本地故障又无法原子证明附件载荷。`DEC-044` 已预留 nullable `LocalMessageId`，但上一切片刻意未开放 unbound 客户端行为。
- **初步决策：** 每个上传使用可重新打开、可精确验证长度的内容源和独立有界 HTTP client，逐个发送恰好一个 multipart `file`。只有受限读取并精确解析出稳定 `AuthenticationRequired` error envelope 的 401，且 token refresh 成功，才允许重新打开并重放一次；HTML、空 body、其他错误码 401、网络/timeout/429/5xx/取消和未知提交一律返回失败，不自动再 POST。
- **初步决策：** 通过严格校验的 201 `AttachmentDto` 先写为当前 AccountScope 的 unbound `LocalAttachments` reservation；全部上传成功后，在一个 SQLite 事务内创建 Image/File pending、mentions 并把规范附件 ID 从 null 绑定到该 `LocalMessageId`。未知、重复、已绑定、跨账户或元数据不一致都回滚；Text 继续要求空附件。
- **初步决策：** durable 恢复边界是 pending 成功提交。此后显式 retry 永远复用原 `ClientMessageId`、AttachmentIds、reply 和 mentions，SendResponse/Realtime/Sync/History 用完整附件元数据提升同一行。pending 之前的进程崩溃不恢复用户意图；每个账户 scope 以独立的进程首次 `UnboundRecoveryCompleted` gate 清除旧 unbound 本地行，失败时复位 gate，同一进程第二个 cache 不得清理第一个 cache 的活跃 reservation；server orphan 由既有 24 小时 lease 回收。部分批次失败只尽力清理本 flight 的本地 unbound 行，不推测或主动重传远端状态。
- **理由：** 该边界既不伪造 upload 幂等，也把真正可重试的消息载荷放入现有账户隔离 SQLite 事务和统一 merge 裁决；不需要改变 server API、schema v2、消息 ID、attach-once 或同步协议。
- **影响：** `DEC-044` 的 nullable 外键从“预留”进入受控使用；Client 增加 upload transport、reservation 写入/清理、附件 pending 与 runtime surface。普通 API 30 秒 client 不变，上传使用独立 10 分钟上界。WPF、进度、跨崩溃草稿恢复、下载和 VPS 仍未开放。
- **来源：** 工程方案第 7.4–7.5、8.2、10.2、12.1–12.3、12.7、14.1–14.2、阶段 9、21.2–21.3；`DEC-017/025/035/041/042/043/044`；`docs/ai/tasks/2026-08-04-stage-9-client-attachment-upload-send.md`；绿色集成头与 Fast 932/932。Claude #73 启动阶段失败，无 job、模型、workspace、费用或结论；Codex reviewer `REVISE` 的两项 P1 已纳入，最终固定差异审查与本机门禁待完成。
