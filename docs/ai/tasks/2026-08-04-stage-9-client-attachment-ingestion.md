# 阶段 9 客户端附件元数据入库

## 任务定义

- **任务名称：** 阶段 9 客户端附件消息接收与账户隔离本地元数据
- **状态：** `已完成`
- **基准提交：** `2fd2f418103fa550275fab9fc039017ee9557b9b`
- **工作分支：** `agent/stage-9-client-attachment-ingestion`
- **相关方案章节：** 3.4、12.3、12.6–12.8、13.1、14、阶段 9、21.2–21.3；`DEC-017/018/026/034/043`

### 目标

让客户端 Realtime、Sync、History/Around 和本地读取能够接收、原子保存并稳定回读服务端已经冻结的 Image/File 附件元数据。数据库升级、不可变重复判定、账户隔离和撤权清理必须 fail-closed，且不在本切片下载或打开任何附件内容。

### 已知事实

- `已验证`：绿色集成头 `2fd2f418` 工作树干净；基准 Fast 为 924/924（Shared 39、Server 255、Client 630、Updater 1）。
- `已验证`：服务端已冻结 Image/File 的 1–10 个唯一附件、按 Guid 规范排序的完整 `AttachmentDto`、固定相对下载路由与空 `ThumbnailUrl`；Send/History/Around/Sync/SignalR 投影一致。
- `已验证`：客户端 HTTP/SignalR JSON transport 已使用共享 `MessageDto`，但 `AccountScopedLocalCache.ValidateIncomingMessage` 明确拒绝任何非空附件，`ToMessageDto` 固定回填空集合，不可变重复比较也未包含附件。
- `已验证`：当前本地 schema 为 v1；`LocalMessages` 与 mentions 已按消息事务写入，撤权删除 `LocalConversations` 后由外键级联删除消息和 mentions；每个 `AccountScopeId` 使用独立数据库路径。
- `已验证`：Claude #71 Opus XHigh 只读 challenge 通过当前兼容 RPC 启动前因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责设计、实现与验证。
- `已验证`：Claude #72 通过本机 Claude Code 2.1.220 以实际 `claude-opus-5`、只读 Read/Glob/Grep 检查固定工作区，完成约六分钟代码与测试取证后被宿主续跑中断，未形成正式结论；两次 session 恢复均在模型调用前连接被拒且为零 token，不能冒充通过或发现。

### 假设

- `假设`：本地 schema v2 按工程方案新增 nullable `LocalAttachments.LocalMessageId` 外键；本切片只写入已绑定消息行，`LocalPath/ThumbnailLocalPath=NULL`、`DownloadStatus=0`，为后续内容缓存保留同一表而不开放对应行为。
- `假设`：客户端只接受与当前服务端冻结投影完全一致的附件元数据：1–255 个有效 Unicode scalar 的安全展示文件名、规范小写 media type、1–100 MiB、精确相对下载路由和空缩略图 URL；任何放宽必须由后续协议决策显式升级。
- `假设`：现有客户端只产生 Text pending，因此非空附件的服务器消息不得提升 Text pending；客户端附件发送另开切片。

### 范围

- 必须实现：
  - 在一个 SQLite 事务内把本地 schema 0/1 升到 v2，同时更新 `PRAGMA user_version` 与 `LocalAppState.SchemaVersion`；拒绝未来版本且迁移失败不留下半升级。
  - 新增 `LocalAttachments` 和消息外键索引；随消息事务插入远端元数据，初始化本地路径为空、状态为未下载。
  - 对类型/数量/唯一规范顺序、文件名、MIME、大小、下载路由和缩略图字段执行严格协议验证；无效远端页在写入前失败。
  - 本地回读携带完整附件集合；不可变重复比较包含全部远端附件字段但忽略本地路径/下载状态；损坏本地附件行 fail-closed。
  - 撤权会话删除级联清理附件元数据，账户相同会话 ID 仍保持物理数据库隔离；覆盖 Realtime、Sync、History/Around 接收路径。
- 允许修改：
  - Client Storage/Sync/Realtime 必要代码与 Client 测试；`docs/ai/` 任务、状态、执行与决策记录。
- 明确不做：
  - 客户端选择/拖拽/粘贴、上传请求与进度、附件发送/pending、下载内容与进度、物理缓存、缩略图/预览/打开、搜索、配额、服务端变化、VPS 实测。

### 验收标准

- [x] v1 数据库原子升级为 v2 且保留消息/会话/状态；注入提交前失败后版本、旧数据和表集合保持 v1；v3 继续拒绝且不降级。
- [x] 合法 Image/File 消息经 Realtime、Sync、History/Around 任一路径可持久化并完整回读；同载荷重复为 Duplicate，任一附件远端字段不同为 Conflict。
- [x] 非法数量、重复/乱序 ID、危险文件名、非规范 MIME、越界大小、外部/错 ID 下载 URL 或非空 ThumbnailUrl 在写前稳定拒绝。
- [x] 本地路径/下载状态不参与远端不可变比较；损坏附件元数据不展示并使当前账户 scope fail-closed。
- [x] 会话撤权后 `LocalAttachments` 随消息级联归零；另一账户相同会话 ID 和附件 ID 的数据不受影响。
- [x] Fast、两次 Full、客户端附件定向重复、八项目漏洞审计、日志脱敏、空白和固定差异审查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Attachment|FullyQualifiedName~AccountScopedLocalCache|FullyQualifiedName~ClientSyncCoordinator|FullyQualifiedName~ClientMessageHistoryCoordinator|FullyQualifiedName~ClientRealtimeConnection"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须放宽为任意远端 URL、允许跨消息复用同一附件 ID、改变 `AccountScopeId`、撤权 tombstone/deny-set、消息不可变比较或服务端协议。
- 必须在本切片下载/执行远端内容、把原文件名用于物理路径，或无法证明 v1→v2 失败时保持原子。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 3.4/12.3/12.6–12.8/13.1/14/阶段 9/21.2–21.3、DEC-017/018/026/034/043、STATUS 和本任务。
只实现附件消息元数据接收：schema 升级、严格验证、消息事务写入、不可变比较、回读、撤权级联和账户隔离。
远端 DownloadUrl 只作为受限协议字段保存，不发起请求；本地路径保持 null，状态保持未下载。
迁移和消息写入均须原子；损坏本地行 fail-closed，任何测试结论以本机实际命令为准。
```

## 任务结果

### 修改摘要

- `AccountScopedLocalCache` 以单个 immediate SQLite 事务把 schema 0/1 原位升级到 v2，原子创建 `LocalAttachments`/索引并同步两个版本标记；未来版本、迁移提交前故障和损坏本地附件都 fail-closed。
- Image/File 的完整附件 DTO 经过数量、规范 Guid 顺序、Unicode 文件名、规范 MIME、大小、精确相对下载路由和空缩略图校验后，与消息/mentions 同事务写入；回读、重复/冲突、pending 提升和撤权级联遵守冻结边界。
- Realtime、Sync、History/Around、账户隔离、并发重复、第二条附件故障回滚和 v1→v2 数据保留均由真实 SQLite/transport 回归覆盖；本切片不访问附件内容。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 924/924；Shared 39、Server 255、Client 630、Updater 1。 |
| `已验证` | 固定代码与边界测试提交 | production `53a5b63`；最终测试头 `722ad49`；`git show --check` 与 `git diff --check` 通过。 |
| `已验证` | 最终 Fast 与两次 Full | 最终 932/932；Shared 39、Server 255、Client 641、Updater 1；Release 构建 0 警告、0 错误，format 通过。受限环境中一次 Fast 仅在 NuGet TLS/凭据恢复处失败；获准网络环境中原命令随后 932/932，通过结果未掩盖该基础设施失败。 |
| `已验证` | Client 附件定向 Release 重复 | 每轮 99 项，连续 10 轮 990/990。 |
| `已验证` | SQLite/协议边界 | v1→v2 保留、提交故障完整回滚、v3 拒绝、消息/mentions/attachments 整体回滚、12 路并发重复、撤权级联与双账户同 ID 隔离、损坏行 fatal 均通过。 |
| `已验证` | 所有协议入口与精确边界 | Realtime、Sync、History/Around 接收通过；10 个附件、255 Unicode scalar 文件名、100 MiB 接受，越界/危险/错路由/冲突在写前拒绝。 |
| `已验证` | 补充门禁 | 八项目依赖漏洞审计、EF model drift、敏感日志/源码检索和空白检查通过。 |
| `已验证` | 独立第二意见 | Claude #71 启动前认证失败；#72 实际 Opus 只读取证但被宿主中断、两次恢复连接拒绝，均无正式结论。Codex 固定差异复核和上述本机门禁为最终依据。 |

### 文件范围

- 新增：本任务记录。
- 修改：`src/RelayCove.Client/Storage/AccountScopedLocalCache.cs`、`src/RelayCove.Client/Storage/ILocalCacheFaultInjector.cs`、Client Realtime/Storage/Sync 测试及 `docs/ai/` 状态、执行和决策记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-044` 已接受 — 本地附件元数据与消息同事务保存，v1→v2 原子升级，严格相对下载路由且当前不触碰内容。
- 已知限制：本切片不提供客户端附件发送、下载、物理缓存或 UI。

### 下一步

- 仅快进集成并清理当前任务分支；随后进入客户端附件上传与 durable Image/File 发送切片。
