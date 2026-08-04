# 阶段 9 客户端附件上传与可靠发送

## 任务定义

- **任务名称：** 阶段 9 客户端上传 reservation 与 durable Image/File 发送
- **状态：** `已完成`
- **基准提交：** `3ceabdc4ba43336aa4f7a00a2fa93c49c2b7806d`
- **工作分支：** `agent/stage-9-client-attachment-upload-send`
- **相关方案章节：** 7.4–7.5、8.2、10.2、12.1–12.3、12.7、14.1–14.2、阶段 9、21.2–21.3；`DEC-017/025/035/041/042/043/044`

### 目标

让 production account runtime 能以可重新打开的本地内容源流式上传 1–10 个附件，并在拿到可信 `AttachmentDto` 后建立账户隔离 reservation、原子创建 Image/File pending、发送和显式重试。任何成功响应或 Realtime/Sync/History 回声都必须以完整附件元数据提升同一行；本切片不接 WPF，也不下载或打开内容。

### 已知事实

- `已验证`：绿色集成头 `3ceabdc` 工作树干净；新分支 Fast 基线 932/932（Shared 39、Server 255、Client 641、Updater 1），Debug 构建 0 警告、0 错误。
- `已验证`：服务端 `POST /api/attachments` 只接受一个名为 `file` 的 multipart section，成功返回 201 `AttachmentDto`；endpoint 没有客户端幂等键，默认单文件 25 MiB、绝对 100 MiB，未绑定 lease 默认 24 小时。
- `已验证`：服务端消息事务已支持 Image/File 的 1–10 个唯一附件 ID、Image 的 `image/*` 约束、owner/null attach-once 与完整附件集合幂等重放；200/201 响应和所有消息投影携带完整规范 DTO。
- `已验证`：客户端 schema v2 已有 nullable `LocalAttachments.LocalMessageId` 与完整远端元数据；但当前只插入已确认消息附件，`PendingMessage/LocalPendingMessage` 没有附件集合，send transport 和 pending promotion 都明确要求 Text/空附件。
- `已验证`：现有 Text 链已经实现 pending-before-POST、同 `ClientMessageId` 显式重试、一次 401 refresh、flight 合流、统一 merge、撤权和 runtime 生命周期；应泛化而不是另建第二条消息发送路径。
- `已验证`：production 普通 API 共用 30 秒 `HttpClient`；该超时不足以作为 25–100 MiB 上传的可靠上界，上传需要独立有界 client，不能扩大所有普通请求的等待时间。
- `已验证`：服务端当前两条 401 路径都在附件提交前结束：authorization challenge 在读取 endpoint body 前返回稳定 `AuthenticationRequired` JSON envelope；`ActorUnavailable` 在读取 body 后、数据库插入前返回同一错误码。反代 HTML、空 body 或其他错误码 401 不能证明未提交。
- `已验证`：Claude #73 Opus XHigh 关键 challenge 仍在兼容 `consult_claude` 启动阶段失败，无 job、模型、workspace、费用或结论；失败不冒充通过。Codex 设计 reviewer 的两项 P1（401 必须精确验证稳定错误 envelope、unbound 启动清理必须采用 scope 进程首次 gate）均已纳入；固定差异 reviewer 的唯一 P2（固定 30 秒/10 分钟 timeout 与禁 307/308 自动重定向）补测后最终 `PASS`，无剩余 P0/P1/P2。

### 假设

- `假设`：本切片输入为 1–10 个可重新打开、可读且可精确验证长度的 seekable 内容源；每项携带安全展示文件名、规范 MIME 和 1–100 MiB 声明长度。Image 要求全部 `image/*`，File 可接受任意既有安全 MIME；消息正文固定 null，允许沿用合法 reply 与规范 mention 集合。
- `假设`：201 响应先以 `LocalMessageId=NULL` 写为当前账户 reservation；全部上传完成后，在一个 SQLite 事务内创建 pending 并以 owner-null 等价条件绑定规范 ID。既有 exact unbound 行可识别为重复，元数据不同、已绑定、未知或重复 ID 均 fail-closed。
- `假设`：上传 endpoint 非幂等，只有受限读取并精确解析出稳定 `AuthenticationRequired` error envelope 的 401，且 refresh 成功后，才可重新打开源并重放一次；HTML、空 body、其他错误码 401、网络/timeout/429/5xx/调用取消均不自动再 POST。无法确定提交结果或部分批次失败时，服务端 orphan 交给既有 lease，客户端只尽力清理本 flight 的 exact unbound 行。
- `假设`：没有 durable 草稿 ID，因此进程在上传成功、pending 创建前崩溃时不能恢复用户发送意图；每个账户 scope 使用独立的进程首次 `UnboundRecoveryCompleted` gate 清除旧进程遗留的全部 unbound 本地 reservation，失败时复位 gate；同一进程第二个 cache 不能删除第一个 cache 的活跃 reservation。服务端副本仍由 lease 回收，已经创建的 pending 必须完整跨重启重试。
- `假设`：production 上传使用独立 10 分钟 `HttpClient` timeout；普通 API 的既有 30 秒行为不变，runtime lifetime/调用取消仍可提前终止上传。

### 范围

- 必须实现：
  - 新增不全量缓冲的 multipart upload transport：严格源元数据/stream 长度、Bearer、受限读取且精确匹配 `AuthenticationRequired` envelope 后的一次 401 refresh、201/Location/`AttachmentDto` 精确校验和稳定状态分类；每次尝试重新打开并确定释放 stream。
  - 将有效上传结果以账户隔离 unbound reservation 落盘；支持 exact 清理与 scope 进程首次恢复清理，使用独立 gate 防止同进程第二个 cache 删除活跃 reservation，失败时复位；损坏、冲突、busy/fatal 都 fail-closed 且日志不含路径、文件名、MIME、token、正文或附件 ID。
  - 扩展 pending 数据模型、创建事务、回读、兼容比较和 retry，使 Image/File 与规范 `AttachmentIds` 绑定；消息/mentions/attachments 任一步失败整体回滚。
  - 泛化既有 send transport/coordinator：Text 行为不回归；Image/File 用 null content、完整 ID/mention/reply 发送，响应附件元数据必须与 reservation 完全一致，Realtime/SendResponse 竞争只提升一次。
  - 在 production `ClientAccountRuntimeFactory`/`ClientAccountRuntime` 暴露 headless attachment-send 调用，并给上传使用独立有界 HTTP client；账户切换、取消、Dispose、撤权与 authentication-required 仍收敛。
  - 覆盖 1/10 项、Image/File、Unicode 名称、边界大小、multipart wire shape、匿名 pre-body 401、`ActorUnavailable` post-body 且零插入、稳定 401→refresh→201、畸形/错误码 401 不重放、非幂等其他失败不重传、部分失败/orphan、原子绑定、同进程第二 cache 与真实进程重启清理、重启 retry、响应冲突、Realtime race、账户隔离和 runtime composition。
- 允许修改：
  - Client Sync/Storage/Accounts 必要代码及 Client 测试；`docs/ai/` 任务、状态、执行与新决策记录。
- 明确不做：
  - WPF 文件选择、拖拽、粘贴截图、进度 UI、caption、缩略图/预览、下载/缓存/打开、服务端协议或 lease 变化、远端 orphan 主动删除/查询、VPS 实测。

### 验收标准

- [x] 合法 Image/File 内容源以恰好一个 `file` section 逐个流式 POST，严格接受对应 201 DTO；1/10 个附件、Unicode 名称、1 byte/100 MiB 客户端边界和规范 MIME 通过。
- [x] 只有受限读取并精确匹配稳定 `AuthenticationRequired` envelope 的 401 最多 refresh/reopen 一次；HTML/空 body/错误码 401、网络、timeout、429、5xx、取消、协议错误或部分批次失败不自动重传，所有打开的 stream 都释放，且没有 pending 半行。
- [x] 每个 201 先形成 unbound reservation；pending 创建在单事务绑定全部规范 ID，未知/重复/已绑定/元数据冲突或故障注入整体回滚；同进程第二 cache 不清除活跃 reservation、真实进程重启会清除旧 unbound，partial/crash orphan 的本地/远端职责有自动化证据。
- [x] Image/File pending、显式 retry 和进程重启始终复用原 `ClientMessageId + AttachmentIds + MentionUserIds + ReplyToMessageId`；Text 继续固定空附件且原测试全绿。
- [x] SendResponse/Realtime/Sync/History 的完整相同附件可提升/重复，任一远端字段变化为 Conflict；本地可变路径/下载状态不影响不可变比较。
- [x] production runtime 使用独立有界上传 client，账户切换/Dispose/撤权/authentication-required 不泄漏 flight；普通 30 秒 HTTP client 行为不变。
- [x] Fast、两次 Full、客户端上传/发送定向重复、八项目漏洞审计、日志脱敏、空白和 Codex reviewer 固定差异审查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~AttachmentUpload|FullyQualifiedName~ClientMessageSendCoordinator|FullyQualifiedName~AccountScopedLocalCache|FullyQualifiedName~ClientAccountRuntime"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须给 upload endpoint 增加幂等键/删除 API、恢复跨崩溃未建 pending 的用户意图、改变 server lease/消息 attach-once、允许任意 URL/MIME/非 seekable 未知长度流，或修改账户 scope。
- 必须把远端未知提交当成功、自动重传非幂等上传、复用已绑定 reservation，或无法证明 pending 与全部附件绑定原子。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 7.4–7.5/8.2/10.2/12.1–12.3/12.7/14.1–14.2/阶段 9/21.2–21.3、DEC-017/025/035/041/042/043/044/045、STATUS 和本任务。
上传非幂等：只有受限读取并精确匹配稳定 AuthenticationRequired envelope 的 401 才 refresh/reopen 一次；其他 401 和所有未知提交不自动重传，stream 每次重新打开并确定释放，响应必须精确验证。
先持久化 unbound reservation，再以同一 SQLite 事务创建 Image/File pending 并绑定全部附件；pending 后的重试只 POST 原键和原集合，不重复上传。
只接 production runtime，不接 WPF/UI/下载/VPS；所有路径、名称、token、正文和 ID 保持日志脱敏。
```

## 任务结果

### 修改摘要

- 新增可重新打开且精确长度的 `ClientAttachmentUploadSource` 与有界 multipart transport；201/Location/完整 DTO 严格验证，只有受限稳定 `AuthenticationRequired` 401 可 refresh/reopen 一次，其他未知提交、timeout、取消、307/308、429/5xx 均不重放。
- 新增统一附件元数据策略、账户隔离 unbound reservation、exact cleanup 与 scope 进程首次恢复 gate；Image/File pending 在一个 SQLite 事务内写 message/mentions 并逐项绑定全部规范附件，故障与冲突整笔回滚。
- 泛化既有 durable send pipeline：Text 保持空附件，Image/File 复用同一 `ClientMessageId + AttachmentIds + mentions + reply` 显式 retry，SendResponse/Realtime/Sync/History 以完整不可变元数据统一提升/冲突。
- production runtime 暴露 headless `SendAttachmentsAsync`；普通请求继续 30 秒，上传使用独立 10 分钟且 `AllowAutoRedirect=false` 的 client，Dispose/process-exit detach 双 client 所有权已固定。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 932/932；Shared 39、Server 255、Client 641、Updater 1；0 警告、0 错误。 |
| `已验证` | 固定代码与测试头 | `44e5010787d1b9fa540f730fa52bef22f25cad02`（主体 `ce81eef` + upload failure 边界补测 `44e5010`）。 |
| `已验证` | 最终 Fast | 980/980；Shared 39、Server 255、Client 685、Updater 1；Debug 0 警告、0 错误。 |
| `已验证` | 最终 Full ×2 | 每轮 980/980；Release 0 警告、0 错误；`dotnet format --verify-no-changes` 与 `git diff --check` 通过。第一次候选 Full 曾仅因 using 顺序停止，修正后从头完成并只计后续两轮。 |
| `已验证` | Client 核心 Release 定向 ×10 | 每轮 147/147，合计 1470/1470；覆盖 upload/cache/coordinator/runtime/composition、10 个 Image、reply/mentions、部分失败、真实 cache 重启 retry、Realtime race、双 client 生命周期。 |
| `已验证` | Server 上传认证/提交前失败定向 | 19/19；匿名 pre-body 与 disabled `ActorUnavailable` 均为稳定 401 且无附件 DB 行。 |
| `已验证` | 非幂等/事务边界 | 稳定 401→refresh→201 恰好两次 open；HTML/空 body/错误码/超限 envelope、network/timeout/cancel/429/5xx/307/308 均单次 POST；stream getter/open/send 所有失败路径释放。unbound gate、unknown/type conflict、逐项 bind 故障、账户隔离与重启清理均通过真实 SQLite。 |
| `已验证` | 审计与独立复核 | 八个项目无已知 vulnerable package；新增日志只记录枚举状态/异常类型且 DTO/source/pending ToString 脱敏；Codex reviewer 修复唯一 P2 后最终 `PASS`、无 P0/P1/P2。Claude #73 启动失败，无结论。 |

### 文件范围

- 新增：上传 source/transport/result/status、统一附件元数据策略、reservation outcome/result、上传 transport 测试与本任务记录。
- 修改：账户缓存与 fault injection、pending models、消息 send transport/coordinator/status、account runtime/interface/factory/composition，以及对应 Client 测试、状态与决策文档。
- 删除：无。

### 决策与限制

- 决策：`DEC-045` 已接受 — 非幂等上传只允许经受限稳定错误 envelope 证明的 401 重放；客户端 unbound reservation 与 pending 原子绑定分离，scope 进程首次 gate 清理旧 unbound，pending 创建后才具备跨重启可靠发送语义。
- 已知限制：不恢复“上传已成功但尚未创建 pending”的用户意图；不接 WPF、进度、下载或 VPS。

### 下一步

- 仅快进集成并清理任务分支；随后进入 WPF 附件选择、发送状态与上传进度切片，仍不读取 VPS 配置。
