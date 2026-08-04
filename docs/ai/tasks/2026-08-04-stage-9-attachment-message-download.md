# 阶段 9 附件消息绑定与授权下载

## 任务定义

- **任务名称：** 阶段 9 attach-once 消息事务、完整投影与会话授权下载
- **状态：** `已完成`
- **基准提交：** `4e4ac09337368db4329e6bc1ae30ddcedb9e5042`
- **工作分支：** `agent/stage-9-attachment-message-download`
- **相关方案章节：** 7.4–7.5、8.2、10.2、11.1–11.2、12.1–12.4、14.1–14.3、阶段 9、21.2–21.3；`DEC-010`、`DEC-014`、`DEC-042`

### 目标

让认证用户把自己已上传的未绑定附件以完整幂等语义一次性绑定到新的 Image/File 消息，并让 Send/History/Around/Sync/SignalR 都返回同一附件集合；随后只允许当前仍可访问附件所属会话的用户读取元数据或下载物理内容。为上传 reservation 增加有界租约，避免长期未绑定内容无限占用磁盘。

### 已知事实

- `已验证`：绿色集成头 `4e4ac093` 工作树干净；附件上传代码检查点 `a2ef8a72` 最终 Fast/两次 Full 为 911/911，Shared 39、Server 241、Client 630、Updater 1。
- `已验证`：Shared 已冻结 `SendMessageRequest.AttachmentIds` 与 `MessageDto.Attachments`；服务端目前只接受 Text/空附件，所有发送、History、Around、Sync 投影固定返回空附件；`NewMessage` 直接发布发送事务返回的 DTO。
- `已验证`：`Attachments.MessageId` 已为 nullable FK，上传行保存 uploader、严格托管 basename、大小/MIME/hash/UTC；上传事务与启动恢复已保证已提交行有物理文件，未绑定内容不公开且不经静态文件中间件。
- `已验证`：消息使用 `(SenderId, ClientMessageId)` INSERT-first 幂等；权限必须先于幂等回读，201 才发布一次，200 replay 不发布；完整 payload 集合不同必须返回 `409 IdempotencyKeyReuse`。
- `已验证`：当前 Client transport/cache 明确拒绝非空附件 DTO，也不发送 AttachmentIds；因此本服务端闭环不能冒充客户端已兼容，客户端选择/缓存/进度必须另开切片。
- `已验证`：Claude #70 Opus XHigh 只读 challenge 通过当前兼容 RPC 等待后仍因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责设计、实现与验证。

### 假设

- `假设`：第一版一条 Image/File 消息包含 1–10 个唯一非空附件 ID；附件 ID 集合无序并按 GUID 规范排序。Text 继续要求附件为空，System 继续不向普通发送端开放。
- `假设`：Image/File 正文可为 null；非 null 时复用 Text 的 1–4000 Unicode scalar/非全空白/控制字符规则。Image 的所有附件必须声明 `image/*`，但 MIME 仍只作展示与类型路由元数据，不构成内容可信证明；File 可携带任意允许的声明 MIME，视频继续按 File。
- `假设`：发送预检只确认所有 ID 存在、属于 actor 且物理文件完整，允许它们已绑定以支持精确 replay；真正的新消息绑定必须在 INSERT 后用 `MessageId IS NULL` 条件更新并检查精确行数，任何已绑定/竞争失败都回滚新消息。
- `假设`：未知、未绑定、已删除会话和当前无权限附件在 metadata/download 上统一返回 `403 ConversationAccessRevoked`，避免 ID 枚举差异；上传者对未绑定附件也不获得下载旁路。
- `假设`：未绑定 reservation 默认 24 小时、配置范围 1–168 小时；后台至少每小时清理一次。先在 SQLite 事务中提交删除过期未绑定行，再尽力删除对应文件；失败或崩溃产生的无行文件由同一严格托管命名 orphan recovery 后续回收。

### 范围

- 必须实现：
  - 扩展 send validator/entity/service/status：Text=0 附件，Image/File=1–10；唯一/非空/规范集合、可选正文、Image 声明 MIME边界。
  - 同一 Serializable 写事务内先复核会话/回复/mentions/附件 owner 与文件完整性，再 INSERT-first；新建消息以条件更新 attach-once，精确行数不符即回滚；重放读取并比较 attachments/mentions 的完整集合。
  - Send、History、Around、Sync 与 SignalR DTO 的附件字段一致、稳定排序、无重复；查询放大有明确固定上界且撤权规则保持不变。
  - 认证 `GET /api/attachments/{id}` 与 `/download`；授权查询必须把附件绑定到当前可见的非删除会话，未绑定/未知/撤权 fail-closed。
  - 下载只从严格托管路径打开 seekable 异步文件流，使用 attachment disposition、`nosniff`、`private, no-store` 与 range；原文件名/路径/hash 不进入日志或错误。
  - 24 小时可配置未绑定租约与周期清理；数据库删行先提交、文件后清理，和 startup orphan recovery 共同覆盖失败/崩溃。
- 允许修改：
  - Shared 必要稳定错误/脱敏，Server Message/Attachment/Data/Endpoints/Services/Hosting/Options/Program/config，Server 测试和必要 `docs/ai/` 记录。
- 明确不做：
  - 客户端选择/拖拽/截图、上传下载进度、本地附件表/缓存/缩略图/预览/打开、病毒扫描/内容嗅探、搜索、去重、配额/管理员设置、VPS 实测。

### 验收标准

- [x] Image/File 对合法 1–10 个 actor-owned 未绑定附件返回 201，附件按规范集合一次绑定；Text 行为完全兼容，System/类型-附件/正文/MIME 非法组合稳定失败。
- [x] 同键同附件 replay 为 200 且不重绑/不再推送；同键不同附件为 409；不同消息并发争抢同一附件最多一个提交，失败方无消息副作用且附件不能被偷换。
- [x] Send/History/Around/Sync/SignalR 返回完全相同且稳定排序的 AttachmentDto；mentions×attachments 组合无重复，固定页上界与原权限/游标/幂等契约不退化。
- [x] metadata/download 对 Public、Private、Direct 当前成员成功；未知、未绑定、删除、禁用、撤权与跨会话猜测 fail-closed，撤权提交后的新请求不能下载。
- [x] 合法下载支持完整与 range，Content-Disposition 安全保留 Unicode 展示名，`nosniff/no-store` 生效；物理丢失稳定 500 且不泄露路径，原名/hash/路径不进日志。
- [x] 过期未绑定行按 lease 删除，绑定行/未到期行/未知文件不动；DB/file 故障与取消遵循安全顺序并可由后续 recovery 收敛。
- [x] Fast、两次 Full、定向重复、model drift、八项目漏洞审计、真实 Kestrel/SQLite/filesystem、日志脱敏和空白检查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Attachment|FullyQualifiedName~Message"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变现有消息服务端 ID、幂等键、Sync cursor、会话授权或删除语义，或需要新 schema/大型依赖才能保证 attach-once。
- 无法证明同键 replay 与不同键附件争抢同时满足 INSERT-first、完整载荷比较和“最多一个绑定提交”。
- 需要对未绑定附件开放下载、允许跨上传者绑定、直接信任 MIME/原文件名、启用 inline 执行或静态目录公开。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 7.4–7.5/8.2/10.2/11/12.1–12.4/14.1–14.3/阶段 9/21.2–21.3、DEC-010/014/042、STATUS 和本任务。
消息附件集合是不可变幂等 payload；先 INSERT 消息，再用 owner + MessageId IS NULL 条件更新，影响行数必须等于规范附件数，否则回滚。
所有消息投影必须返回同一附件集合；下载授权必须从 attachment -> message -> 当前可见 conversation 一次绑定，未绑定与不可访问统一 fail-closed。
原文件名只用于安全 Content-Disposition，物理路径只由严格托管 basename 计算；TTL 删除先提交 DB，再尽力删文件并由 orphan recovery 补偿。
```

## 任务结果

### 修改摘要

- 普通发送端现支持 Image/File 的 1–10 个唯一附件；同一 Serializable 事务保持原 INSERT-first 幂等顺序，并在新消息插入后以 owner + `MessageId IS NULL` 条件更新完成 attach-once，精确 replay 与载荷冲突分别返回 200/409。
- Send、History、Around、Sync 与真实 SignalR NewMessage 均返回同一按 GUID 排序的附件 DTO；mentions 与 attachments 的 SQL 组合投影在内存分组时分别去重，既有固定消息页上界、游标与撤权语义不变。
- 新增认证 metadata/download；授权查询从附件绑定到消息及当前可见会话，未知、未绑定、删除或撤权统一 403。下载使用严格托管路径、attachment disposition、seekable 异步流、range、`nosniff` 与 `private, no-store`。
- 未绑定 reservation 默认保留 24 小时、配置范围 1–168 小时；启动及每小时维护以 500 行短事务先提交删行，再尽力删精确托管文件，失败残留由启动 orphan recovery 收敛。文件删除异常只记录类型，不泄露本地路径。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 911/911；Shared 39、Server 241、Client 630、Updater 1。 |
| `已验证` | 固定代码提交最终 Fast 与两次 Full | `41f7d11e207fd984bbc3e2a8c003f9bf2ed6a2e9` 为 924/924；Shared 39、Server 255、Client 630、Updater 1；Debug/Release 均 0 警告、0 错误，format 与 `git diff --check` 通过。 |
| `已验证` | Attachment/Message Release 定向集连续 10 轮 | 每轮 96 项，共 960/960；另有最大 10 附件、真实 SignalR payload、并发争抢、精确重放/冲突和完整投影的专用回归。 |
| `已验证` | 授权下载与 lease 故障边界 | Public/Private/Direct、删除/撤权/未知/未绑定、完整与 Range、Unicode disposition、物理缺失/占用的 500 脱敏均通过；过期/绑定/未到期/未知文件、取消、SQLite exclusive lock、文件删除失败与启动补偿均通过。 |
| `已验证` | 真实 Kestrel + 临时 SQLite/filesystem | EF CLI 实际应用全部 migration；真实发送 201、精确 replay 200、改附件 409、未绑定 metadata 403、授权 metadata/完整下载 200、Range 206、匿名 401；两个托管文件逐字节一致且无 staging，隔离目录与临时脚本均精确清理。 |
| `已验证` | 模型、依赖与脱敏 | `has-pending-model-changes` 无漂移；八项目直接/传递依赖无已知漏洞；真实宿主日志未出现原名、托管名、hash 或上传路径。Claude #70 XHigh 因本机认证源优先级失败，无 job、模型、workspace、费用或结论，未冒充审查通过。 |

### 文件范围

- 新增：附件访问结果/查询/DTO factory、周期维护 hosted service、附件消息与下载端到端测试。
- 修改：Server Message/Attachment endpoint、command/query/sync/DTO/path/recovery/options/DI/config，以及消息、恢复、options、SignalR 测试和本任务/状态/执行/决策记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-043` — 附件集合是不可变消息幂等载荷；新消息以 INSERT 后的 owner/null 条件更新 attach-once，下载每次从当前会话权限重新授权，未绑定 lease 采用 DB-first 删除与启动补偿。
- 已知限制：本切片只完成服务端；现有 Client 仍拒绝非空附件 DTO，客户端发送、缓存与 UI 必须在下一切片接入后才可宣称端到端附件可用。

### 下一步

- 仅快进集成本切片；随后冻结客户端附件 transport、本地缓存与最小 UI 纵向边界，继续不读取 VPS 配置。
