# 阶段 9 服务端附件上传与持久存储

## 任务定义

- **任务名称：** 阶段 9 认证附件上传、物理文件与 SQLite 元数据闭环
- **状态：** `已完成`
- **基准提交：** `556efd49acdf707fd433152d60ee130084939825`
- **工作分支：** `agent/stage-9-attachment-upload`
- **相关方案章节：** 3.4、7.5、8.1–8.2、10.2、11.1–11.2、14.1–14.2、17.2、18.1、18.4、阶段 9、21.3；`DEC-003`、`DEC-004`、`DEC-010`

### 目标

让正常认证用户可以通过 `POST /api/attachments` 上传恰好一个有界文件；服务端以流式、不可遍历的方式把内容写入专用目录，同时事务化保存不可变附件元数据并返回脱敏 `AttachmentDto`。该切片只建立未绑定附件，下载权限和消息绑定由后续切片完成。

### 已知事实

- `已验证`：绿色集成头 `556efd49` 工作树干净，Fast 基线 860/860；Shared 37、Server 192、Client 630、Updater 1。
- `已验证`：Shared 已有 `AttachmentDto`，`SendMessageRequest/MessageDto` 已携带附件集合；服务端和客户端当前都严格要求附件集合为空，所有消息投影也固定返回空集合。
- `已验证`：当前服务端没有 `Attachment` 实体、`Attachments` 表、上传 options、文件存储服务或附件 endpoint；方案冻结的表字段为 nullable `MessageId`、上传者、原始/存储文件名、类型、大小、SHA-256 与创建时间。
- `已验证`：当前 JWT `TokenValidated` 会按数据库动态拒绝缺失/禁用用户；现有 API 使用统一错误 envelope、SQLite busy/locked 503 和脱敏结构化日志。
- `已验证`：Microsoft ASP.NET Core 10 官方文档建议大文件使用 `MultipartReader` 流式处理、由宿主限制总请求且由 `BodyLengthLimit` 限制每个 section，并明确禁止把不可信原文件名用于物理存储；官方限流示例要求依赖认证身份时把 `UseRateLimiter` 放在 `UseAuthentication` 之后（2026-08-04 查阅）。
- `已验证`：Claude #69 Opus XHigh 只读上传架构/安全 challenge 通过当前兼容 RPC 等待后仍因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责设计、实现和验证。

### 假设

- `假设`：首版单附件默认上限 25 MiB、部署硬上限 100 MiB；上传按已认证 subject 固定窗口限流，默认每分钟 10 次且不排队。
- `假设`：原始文件名只作为最多 255 个 Unicode scalar 的展示元数据，拒绝控制字符、`/`、`\\`、空白名和 `.`/`..`；物理文件使用无扩展名的服务端随机 basename，声明 MIME 只作有界元数据且不产生信任。
- `假设`：上传根目录是受信任的运维配置边界，不由请求选择，不经静态文件中间件公开；相对配置按 content root 解析，生产部署在后续阶段覆盖为 `/opt/relaycove/data/uploads`。
- `假设`：未绑定附件只能由上传 endpoint 创建且暂不可下载；成功但未绑定的长期清理由后续“消息绑定/附件租约”切片冻结，本切片只清理由当前请求失败、取消或数据库回滚产生的暂存/最终文件，并在启动时回收严格命名且无数据库行的崩溃残留。

### 范围

- 必须实现：
  - `StorageOptions/UploadOptions` 与启动校验：上传根、1–100 MiB 单文件限制、subject 级上传限流；明确请求总量、multipart boundary/header/section 上界。
  - `Attachment` 实体、关系、SQLite CHECK/索引与真实 migration；保留旧认证/会话/消息数据并验证升级/降级与 model drift。
  - 认证的单文件 multipart streaming：只接受一个名为 `file` 的文件 section，不走 `IFormFile` 全量缓冲，不接受额外字段/文件。
  - 生成 basename、`CreateNew` 暂存、流中再次限长并计算 SHA-256；原始文件名和 MIME 不进入物理路径、日志、错误或 `ToString()`。
  - 文件/数据库提交顺序：暂存完成后在同一 SQLite 写事务复核 actor 正常、插入元数据、同目录无覆盖 rename、再提交；所有可观察失败和取消清理本次文件，启动恢复只处理严格托管命名的无行残留。
  - 201 `AttachmentDto`、稳定 400/401/413/429/500/503 边界和真实 HTTP/SQLite/filesystem 自动化。
- 允许修改：
  - Shared 附件脱敏/错误码，Server Options/Data/Endpoints/Services/RateLimiting/Program/config，Server 测试和必要 `docs/ai/` 记录。
- 明确不做：
  - 消息携带附件、Image/File 消息开放、下载/元数据 GET、会话授权、客户端选择/拖拽/截图/进度、缩略图/预览/打开、病毒扫描、内容嗅探、去重、配额/管理员设置、长期未绑定附件回收、VPS 实测。

### 验收标准

- [x] 真实 multipart HTTP 对合法二进制、Unicode 名称和缺省/规范 MIME 返回 201；DB 的大小/hash/上传者/创建时间与不可预测 basename 正确，磁盘字节完全一致且原始名从未进入路径。
- [x] 匿名/禁用用户、错误 content type/boundary、零文件、空文件、错字段、额外字段/文件、恶意/超长名称、非法 MIME、超限、限流与取消得到稳定结果，且不留下 DB 行或文件。
- [x] 请求总量、section、header 与流复制均有独立上界；exact-limit 成功、limit+1 为 413，慢/未知长度流不能绕过。
- [x] DB busy/保存失败、目标冲突、磁盘异常与取消保持“无已提交坏行”；正常异常路径清零暂存，启动恢复删除严格命名的无行残留但不触碰未知文件。
- [x] migration up/down 保留既有数据并固定 GUID/UTC/长度/hash/FK/CHECK/索引；Fast、两次 Full、定向重复、model drift、八项目漏洞审计、日志脱敏和空白检查通过。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Attachment"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要开放附件下载/消息绑定、接受多个文件或表单字段、使用请求提供的物理路径、加入扫描/云存储/大型依赖，或必须改变既有消息 ID/幂等/同步顺序。
- 无法同时证明请求/section/落盘三重限长，或失败可留下已提交但物理文件缺失的附件行。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 3.4/7.5/8.2/10.2/11/14/18.4/阶段 9/21.3、docs/ai/STATUS.md 和本任务。
只实现认证单文件上传、物理存储和 nullable-message 附件元数据；不开放下载或消息附件。
用 MultipartReader 流式处理；请求、section、header 和写入字节分别有界，原文件名永不进入物理路径或日志。
暂存、SQLite 行、无覆盖 rename 与 commit 的顺序必须避免提交缺文件的行；失败/取消清理，崩溃残留只按严格托管命名恢复。
```

## 任务结果

### 修改摘要

- 新增认证的 `POST /api/attachments`，以 `MultipartReader` 只接受一个 `file` section；总请求、boundary、header、section 与实际复制分别有界，超限稳定返回 `AttachmentTooLarge`。
- 内容以 `CreateNew` 流入同目录随机暂存文件并同步计算 SHA-256；原始文件名只作严格有界展示元数据，物理 basename 无扩展、不透明且不可覆盖。
- 新增 `Attachment` 实体、关系、SQLite CHECK/索引与真实 migration；SQLite 写事务在提交前完成无覆盖 rename，actor 状态在事务内复核，busy/冲突/取消不产生已提交坏行。
- 新增启动恢复，只删除严格托管命名的暂存文件和无数据库行最终文件；数据库行缺文件时 fail-closed，未知文件和已跟踪文件保持不动。
- 新增 subject 固定窗口上传限流、启动 options 校验、宿主 413 错误映射与 DTO/日志脱敏；匿名请求绕过上传 limiter 后仍稳定由授权层返回 401。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 绿色集成头 Fast 基线 | 860/860；Shared 37、Server 192、Client 630、Updater 1。 |
| `已验证` | 固定代码提交最终 Fast 与两次 Full | `a2ef8a72f24829e61f5ae8e34aa3b661ce90fd0d` 为 911/911；Shared 39、Server 241、Client 630、Updater 1；Debug/Release 均 0 警告、0 错误，format 与 `git diff --check` 通过。 |
| `已验证` | Attachment Release 定向集连续 10 轮 | 每轮 39 项，共 390/390；覆盖真实 HTTP/SQLite/filesystem、解析边界、限流、冲突回滚、取消与启动恢复。 |
| `已验证` | 真实 Kestrel + 临时 SQLite/filesystem | EF CLI 实际应用全部 migration；8 字节 configured exact-limit 返回 201，70 KiB multipart 触发宿主级稳定 413；仅一个严格命名、字节一致的最终文件，无暂存残留；隔离目录随后精确清理。 |
| `已验证` | migration、模型与依赖安全 | 自动化 up/down 保留既有认证/会话数据且固定表/列/FK/CHECK/索引；`has-pending-model-changes` 无漂移；八项目直接/传递依赖无已知漏洞。 |
| `已验证` | 脱敏与独立审查边界 | 敏感 logger 检索无命中，响应/result/staged DTO 的 `ToString()` 脱敏；Claude #69 XHigh 因本机认证源优先级失败，无 job、模型、workspace、费用或结论，未冒充审查通过。 |

### 文件范围

- 新增：Attachment entity/migration、上传 endpoint、streaming reader、staged/path/command/recovery services、storage/upload options、上传限流及对应 Shared/Server 测试、本任务记录。
- 修改：Server DbContext/entity relationships/Program/config/错误中间件、Shared error/DTO 脱敏、测试工厂与状态/执行/决策记录。
- 删除：无。

### 决策与限制

- 决策：`DEC-042` — 未绑定附件只作为上传者的不可下载 reservation；内容经多层有界 streaming 进入非静态随机文件，文件在 SQLite commit 前发布，崩溃歧义由严格启动恢复收敛。
- 已知限制：本切片上传的附件尚不能下载或发入会话；内容未扫描且未绑定附件的长期租约/回收尚未实现，因此物理内容保持非静态、不可下载状态。

### 下一步

- 仅快进集成本切片；随后冻结并实现附件 attach-once 消息事务与会话授权下载，仍不提前开放客户端或 VPS Gate。
