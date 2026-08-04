# 阶段 9 WPF 附件下载交互与受控本地访问

## 任务定义

- **任务名称：** 阶段 9 已确认附件行、下载进度、取消/重试与受控本地访问
- **状态：** `进行中`
- **基准提交：** `c53f5c9ef93f6924fbde7728df01a51a7be05505`
- **工作分支：** `agent/stage-9-wpf-attachment-download`
- **相关方案章节：** 9.2–9.4、14.3–14.5、阶段 9、21.3；`DEC-034/038/043/044/048`

### 目标

把已完成的可信附件下载核心接入 production WPF 消息列表：已确认的 Image/File 消息显示严格投影的附件名、大小和本地状态；用户可以显式下载、查看真实接收进度、取消自己发起的 flight、失败后重试，并在每次重新验证物理内容与当前授权后对已下载文件执行受控本地访问。所有 UI 回调都必须抵御会话切换、撤权、账户重建和 A→B→A 的迟到结果，且不得把本地路径、URL、hash 或 ID 暴露给 presentation、日志或自动化名称。

### 已知事实

- `已验证`：基线、本分支起点与远端 `agent/v1-integration` 都是 `c53f5c9ef93f6924fbde7728df01a51a7be05505`；上一核心切片最终 Fast/Full 1154/1154，真实 Kestrel→Client→磁盘→SQLite 通过。
- `已验证`：`MessageDto.Attachments` 已在本地消息读取时经过严格元数据校验，但当前 presenter 只把 Image/File 显示为 `[图片]`/`[文件]`，完全未投影附件集合。
- `已验证`：runtime/shell 已提供 selection-token 绑定的 `DownloadAttachmentAsync`；结果包含稳定状态和受控相对路径，进度只表示写入 staging 的接收字节。
- `已验证`：`LocalAttachments.DownloadStatus/LocalPath` 是持久真源，但当前消息页 outcome 未投影 Downloaded 状态；WPF 重启后无法直接显示已下载。
- `已验证`：物理缓存文件是只由 conversation/attachment/full SHA-256 决定的 opaque `*.cache`；原名和 MIME 不决定路径，不能直接依赖 `.cache` 的系统文件关联，也不能为打开而创建不受撤权管理的临时副本或硬链接。
- `已验证`：消息列表使用 recycling virtualization；现有 snapshot revision、selection object identity、composer context version 和安全链接 current-membership gate 可复用。
- `已验证`：Codex 三路只读探索已完成 WPF、竞态、UIA 与 Windows shell 边界映射；关键安全 challenge 已在本机只读 Claude Opus/XHigh 后台任务 `a28c24d7` 启动，Codex 不串行等待其结果。

### 范围

- 必须实现：
  - confirmed Image/File 附件 presentation：安全展示名、格式化大小、本地下载状态、稳定顺序和全脱敏 `ToString()`；pending 不伪造可下载元数据。
  - 本地消息页同一授权事务投影 Downloaded attachment ID 集，并在 selection/presenter 中保持重启可见；不暴露 `LocalPath`。
  - WPF 每附件下载、真实进度、owned cancel、失败重试、完成状态和可访问 live region；同附件未知外部 flight 不显示可取消所有权。
  - 基于 exact selection/message/attachment membership、context version 与不可复用 flight identity 的迟到回调门；切换、非 Ready、撤权、注销和退出取消并清空旧状态。
  - 已下载内容的受控本地访问：WPF 只提交 attachment identity；内部重新验证严格托管相对路径、scope、ID/hash、非 reparse、长度/SHA 和最终授权/DB 状态；shell 只接收不可伪造且脱敏的已验证能力对象。
  - 自动化覆盖 presenter、事务投影、A→B→A、进度/取消/重试、UIA/键盘和 shell 不执行任意路径。
- 允许修改：Client `Accounts/`、`Storage/`、`Sync/`、Windows interop/WPF 与对应测试；必要 `docs/ai/` 记录。
- 明确不做：图片缩略图/原图查看、Save As、Range/resume、自动驱逐、搜索、服务端/Shared 协议、schema/migration、VPS/双客户端、真实执行潜在恶意附件的自动化。

### 冻结安全边界

- 第一检查点先交付“在文件夹中显示”，优先使用 `SHOpenFolderAndSelectItems`，不拼接 explorer/cmd/PowerShell 参数；直接打开文件只有在关键 challenge 与本地复算证明 opaque `.cache`、文件关联、MOTW/Attachment Manager 和撤权清理均可闭合后才进入本任务，否则拆为紧随其后的独立安全切片。
- WPF、presentation 和 shell 公共 surface 不接收任意路径。cache store 在验证后产生内部 capability，final access/state/path 复核尽量贴近 shell 调用；shell 已启动后的撤权无法召回外部进程，只保证受控缓存清理。
- 不创建以原文件名或扩展名决定路径的副本、硬链接或持久导出；不修改 `DEC-048` 的受控缓存命名与配额边界。

### 验收标准

- [ ] confirmed 附件按协议顺序显示安全文件名、大小和持久 Downloaded 状态；Text/System/pending 不产生伪附件动作，presentation/UIA/log 不含路径、URL、hash 或 ID。
- [ ] 每附件下载进度单调；用户只可取消自己发起的 flight；Canceled/失败可重试，Completed/AlreadyDownloaded 才进入已下载状态；quota/auth/revoke/protocol/local/remote 状态有明确且不泄密的文案。
- [ ] A→B→A、snapshot refresh、recycling、旧 progress/result、撤权/注销/退出均不能污染当前行；同一消息多附件互不串状态，焦点与 live region 可用且不过度播报。
- [ ] 本地访问必须从 identity 重新读取/验证 cache 与最终授权；绝对路径、遍历、ADS、`.part`、错 ID/hash、缺失/损坏/reparse、DB 变化或撤权均零 shell 调用。
- [ ] 定向、STA WPF、Fast、最终 Full、Codex reviewer、关键 Claude challenge、model drift、依赖漏洞、脱敏与空白检查完成；真实登录附件视觉/Narrator 与 VPS 保持 `未验证` 到 M5。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Attachment|FullyQualifiedName~MessageListPresenter|FullyQualifiedName~AccountShell"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须把任意路径、原名、URL、hash 或 token 暴露给 shell/日志，必须生成不可随撤权清理的副本/硬链接，或必须自动执行未经过 Windows 附件安全策略的下载内容。
- 必须改变 Shared/Server 协议、数据库 schema/migration、受控 cache 命名/quota，或无法阻止旧 selection/flight 的迟到回调污染当前 UI。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案、DEC-034/038/043/044/048、STATUS 和本任务。
只把 confirmed 严格附件元数据与持久 Downloaded 状态接入 WPF；所有动作以当前 Ready snapshot membership、context version 和 flight identity 重新验证。
WPF 永不接收 LocalPath；本地访问必须经 cache 物理复验、最终授权复核和不可伪造 capability。普通审查用 Codex reviewer，Claude 只挑战本关键安全边界。
```

## 任务结果

进行中；完成后补充修改摘要、验证证据、限制与下一步。
