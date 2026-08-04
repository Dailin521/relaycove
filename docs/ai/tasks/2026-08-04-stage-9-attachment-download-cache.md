# 阶段 9 客户端附件可信下载与账户隔离缓存核心

## 任务定义

- **任务名称：** 阶段 9 已确认附件的可信全量下载、账户隔离缓存与原子本地状态
- **状态：** `进行中`
- **基准提交：** `b678865e2f2a559e9e620567a5caacb4a5882ae0`
- **工作分支：** `agent/stage-9-attachment-download-cache`
- **相关方案章节：** 3.4、9.3–9.4、12.7–12.8、14.3–14.5、阶段 9、21.3；`DEC-017/018/043/044/045/047`

### 目标

让当前账户对已确认 Image/File 消息附件执行一次受权全量 GET，在不阻塞 UI 的前提下把响应流写入 `AccountScopeId` 隔离的受控缓存，以服务端已有 SHA-256 强 ETag、精确大小和客户端流式 hash 同时验证内容；只有同卷原子发布和 SQLite 条件提交都成功后，`LocalAttachments.LocalPath/DownloadStatus` 才表示可用缓存。提供真实接收字节进度与 runtime/shell surface，供下一 WPF 下载交互切片直接使用。

### 已知事实

- `已验证`：绿色集成头、本分支基线与远端 `agent/v1-integration` 都是 `b678865e2f2a559e9e620567a5caacb4a5882ae0`；新分支 Fast 1088/1088（Shared 39、Server 255、Client 793、Updater 1），Debug 构建 0 警告、0 错误。
- `已验证`：服务端下载 endpoint 已按 attachment → message → 当前会话权限授权，支持完整/Range、`private, no-store`、`nosniff`，但授权投影和响应尚未携带数据库已有的 SHA-256。
- `已验证`：Client schema v2 已预留 `LocalAttachments.LocalPath`、`ThumbnailLocalPath` 与 `DownloadStatus`；当前只插入未下载状态，既无状态读写 API，也无物理缓存、下载 transport 或下载 coordinator。
- `已验证`：`AttachmentDto.DownloadUrl` 已被 Client 严格限制为同源精确 `/api/attachments/{id:D}/download`，大小为 1–100 MiB；未绑定上传 reservation 与已确认消息附件可由 nullable `LocalMessageId` 区分。
- `已验证`：production 本地根是 `%LOCALAPPDATA%\RelayCove`，账户数据库位于 `Accounts/<AccountScopeId>`；工程方案要求物理缓存位于 `cache` 并继续按同一 `AccountScopeId` 隔离。
- `已验证`：Claude Code 2.1.221 关键只读 Opus/XHigh challenge 已作为后台 agent `a4e60acf` 启动，无费用或时间上限，工具限于 Read/Glob/Grep；Codex 不等待其串行推进。

### 假设

- `假设`：强 ETag 使用数据库已有 lowercase SHA-256 的 quoted hex，只在授权成功的完整/Range 响应中暴露；这是 additive 响应头，不进入 DTO、日志或错误。Client 第一版只发无 Range 的全量 GET，拒绝 206，不做断点续传。
- `假设`：物理缓存根为 `%LOCALAPPDATA%/RelayCove/cache/<AccountScopeId>`，使用扁平固定格式的 conversation/attachment/hash `.cache` 名和同目录随机 `CreateNew` `.part`；原文件名、MIME、URL 和数据库任意路径都不能决定物理位置。
- `假设`：持久状态冻结为 0 NotDownloaded、1 Downloading、2 Downloaded、3 Failed；网络 I/O 期间不持有 SQLite gate。启动把遗留 Downloading 复位，双向 reconcile DB/文件，删除严格托管 orphan/temp，缺失、长度/hash 不符的 Downloaded 复位为未下载。
- `假设`：每账户缓存硬上限先固定 1 GiB；达到上限显式失败，不自动驱逐、不引入 LRU/schema v3。后续设置/清理 UI 可另行演进，但本切片不得形成无界磁盘增长。
- `假设`：同一账户附件以短 SQLite claim 保证单底层 GET；重复调用稳定返回 InProgress。调用取消、runtime 终止或会话撤权会取消 I/O；撤权先建立 deny-set 并取消 flight，durable tombstone/DB cascade 后才尽力删除该会话严格托管文件。

### 范围

- 必须实现：
  - 服务端授权下载响应的强 ETag，完整与 Range 使用同一 SHA-256 validator；未授权响应不泄露 hash。
  - Client 独立长时、禁 redirect 下载 HTTP 边界：Bearer、`ResponseHeadersRead`、一次稳定 401 refresh、稳定/非稳定 403 分类、仅 200、精确 Content-Length/最终长度、流式 SHA-256 与单调接收进度。
  - `AccountScopeId` 隔离的扁平受控缓存 store：路径/重解析点防护、随机 staging、flush、同卷无覆盖原子 move、1 GiB quota、精确删除与脱敏结果。
  - LocalAttachments 下载状态读取/claim/失败/完成 CAS、相对路径严格格式、已确认消息与当前会话权限门、启动恢复和 DB↔文件 reconcile。
  - 同附件 single-flight、账户终止/撤权取消、durable purge 后物理清理、runtime/shell surface 与自动化故障注入。
- 允许修改：
  - Server 附件授权投影/endpoint 与对应测试；Client `Storage/`、`Sync/`、`Accounts/`、composition/runtime/shell 与对应测试；必要 `docs/ai/` 记录。
- 明确不做：
  - WPF 附件行、下载按钮/视觉进度、打开文件/目录、Save As、图片缩略图/原图查看、Range/resume、跨重启 partial、后台预取、自动驱逐/LRU、搜索、病毒扫描/内容嗅探、Shared DTO、数据库 schema/migration、VPS。

### 验收标准

- [ ] 授权完整/Range 响应携带同一合法强 SHA-256 ETag；匿名、未知、未绑定、删除或撤权响应不泄露 ETag/hash，既有授权/Range 行为不回归。
- [ ] Client 只请求严格同源 route 且不跟随 redirect；完整 200 必须同时通过 ETag、metadata Size、Content-Length（若存在）、实际 EOF 长度与流式 SHA-256，任何失败不发布可见文件或成功 DB 状态。
- [ ] staging → flush → atomic move → SQLite CAS 顺序经故障注入与重启 recovery 验证；DB 不指向 partial/缺失/越界/错误 hash 文件，orphan/temp 可恢复清理，缓存路径不受原名/远端/损坏 DB 控制。
- [ ] 1 GiB quota、两个账户同 attachment ID 隔离、同附件并发单 GET、取消/退出、稳定撤权 headers/midstream/publish 竞态均 fail-closed；普通 403/网络/429/5xx 不触发破坏性 purge。
- [ ] 接收进度只表示写入 staging 的响应内容字节，单调且不冒充 hash/原子发布/SQLite 完成；回调异常隔离，日志、错误与 `ToString()` 不含 token、URL、ID、原名、MIME、hash 或路径。
- [ ] 定向、Fast、最终一次 Full、真实 Kestrel/磁盘/SQLite 场景、Codex reviewer、关键 Claude challenge、model drift、依赖漏洞、日志脱敏与空白检查完成。

### 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Attachment"
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~AttachmentDownload|FullyQualifiedName~AttachmentCache"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
git diff --check
```

### 停止并询问

- 必须把 hash、token、原始文件名或路径加入不受权协议/日志，必须信任远端/数据库路径，或无法避免 DB 指向未完整验证文件。
- 必须新增 schema/migration、自动执行下载内容、跨账户复用缓存、开启自动 redirect/Range resume，或固定 quota 会显著改变已冻结产品体验。
- 无法保证撤权取消与 durable tombstone/物理删除的顺序，或清理必须递归跟随未知目录/重解析点。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 3.4/9.3–9.4/12.7–12.8/14.3–14.5/阶段9/21.3、DEC-017/018/043/044/045/047、STATUS 和本任务。
只做受权全量下载、强 ETag/hash、账户隔离物理缓存、LocalAttachments 原子状态、quota/recovery/revocation 和 runtime/shell 核心；WPF/open/thumbnail 留给后续独立切片。
网络 I/O 不持有 SQLite gate；只允许 temp 验证完成后原子发布，再以短 CAS 提交 DB。任何迟到、取消、撤权、崩溃和损坏状态都 fail-closed。
普通审查由 Codex reviewer；Claude 只做本关键路径的一次只读 challenge。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 新分支 Fast 基线 | 1088/1088；Shared 39、Server 255、Client 793、Updater 1；0 警告、0 错误。 |
| `进行中` | Claude 关键 challenge | 后台 agent `a4e60acf`，只读 Opus/XHigh，无费用/时间上限；等待终态。 |
| `未验证` | 本任务最终门禁 | 实现完成后填写。 |

### 文件范围

- 新增：待完成。
- 修改：待完成。
- 删除：待完成。

### 决策与限制

- 决策：待 Claude challenge、实现与 Codex reviewer 收敛后记录为 `DEC-048`。
- 已知限制：WPF 下载视觉、打开/目录、缩略图/原图、Range/resume、自动缓存驱逐和 VPS 留在后续切片。

### 下一步

- 完成本 core 后接 WPF 附件展示、下载/取消/失败重试与安全打开；随后完成本地缩略图/有界原图查看并关闭 M2。
