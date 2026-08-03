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
- **决策：** 启动 Restore 只读取一个凭据并发一次 refresh，响应 user ID 必须等于持久 user ID；401 或身份错配同时清内存和凭据。附加 store 的会话在后续 rotation 中先尝试保存新 refresh token，再原子发布内存响应；保存失败清旧凭据并标记未持久化。logout 先清内存与凭据；若凭据清除失败，即使调用者取消也仍以会话生命周期尝试一次远端 revoke，并返回 `CredentialClearFailed`，不重试。
- **理由：** 服务端 rotation 是唯一权威提交点，客户端无法把网络与 DPAPI 合成分布式事务。保留当前已验证内存会话维持可用性，同时删除已知失效磁盘 token并显式降级持久状态；logout 的条件性不可取消 revoke 缩小“有效 token 留在不可删除文件中”的窗口。
- **影响：** Client 增加串行持久认证入口、内部 Restore HTTP 和会话 credential persistence 状态，扩展客户端 logout 状态；不改变 Shared/服务端协议、DPAPI 格式或依赖。进程在服务端轮换成功但收到响应前终止仍只能在下次 401 时清理旧 token，这是单次使用 rotation 的固有限制。
- **来源：** `DEC-006`、`DEC-022`、`DEC-023`；工程落地方案第 9.3、12.5、18.1；`docs/ai/tasks/2026-08-03-stage-6-client-session-restore.md`；当前 ClientAuthenticationSession 与 ClientCredentialStore 实现。
