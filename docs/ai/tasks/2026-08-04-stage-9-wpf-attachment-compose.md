# 阶段 9 WPF 附件选择与上传进度

## 任务定义

- **任务名称：** 阶段 9 WPF 本地文件选择、Image/File 发送状态与上传进度
- **状态：** `进行中`
- **基准提交：** `f8e058e9e89d80686a9b15cd955e02f900bfa703`
- **工作分支：** `agent/stage-9-wpf-attachment-compose`
- **相关方案章节：** 2.1、9.2–9.4、12.1–12.3、14.1–14.2、阶段 9、21.3；`DEC-017/025/035/041/042/043/044/045`

### 目标

让当前 Ready 会话可以从原生多选文件对话框选择 1–10 个本地文件，以 Image 或 File 消息走既有非幂等上传、reservation、durable pending 与显式 retry 链；WPF 显示客户端把真实文件字节复制进 HTTP 请求内容的单调批次进度，并明确区分上传前失败与 pending 后失败。本切片不把客户端字节进度冒充服务端已持久化。

### 已知事实

- `已验证`：绿色集成头 `f8e058e9e89d80686a9b15cd955e02f900bfa703` 工作树干净；新分支 Fast 基线 980/980（Shared 39、Server 255、Client 685、Updater 1），Debug 构建 0 警告、0 错误。
- `已验证`：production runtime 已暴露 headless `SendAttachmentsAsync`，支持 Image/File、1–10 个可重新打开且精确长度的 source、reply/mentions；上传后 reservation、pending 原子绑定、消息发送与原键 retry 已完成。
- `已验证`：account shell 与 WPF 仍只接 Text；现有组合器已有 Ready selection、reply/mention、context version、pending 提交后条件清理和迟到结果不覆盖新输入的模式。
- `已验证`：上传 transport 以 `StreamContent` 读取 source，但没有进度信号；服务端只在 multipart 读取、暂存和事务提交后返回最终 201，不提供服务端进度协议。
- `已验证`：服务端默认单文件上限 25 MiB、部署硬上限 100 MiB；客户端 source 只冻结 1–100 MiB 绝对边界，因此本地选择成功不代表服务器一定接受，413 必须保持可见。
- `已验证`：普通审查按用户要求由 Codex reviewer 子代理承担；本切片不改变公共协议、数据库、安全或上传重放规则，不调用 Claude。

### 假设

- `假设`：本切片以原生 `OpenFileDialog` 多选为唯一入口；选择结果全有或全无，合计最多 10 个、路径规范后不重复。路径只保留在内存 source closure，不写 SQLite、日志、错误或 `ToString()`。
- `假设`：扩展名只生成不可信的声明 MIME。常见受控图片扩展映射为规范 `image/*`；全部选项都是受控图片时发送 Image，否则发送 File，视频始终按 File；未知扩展回退 `application/octet-stream`。不嗅探内容、不解码图片，也不把声明 MIME 当可信内容类型。
- `假设`：当前 Client 只允许附件消息正文为 null。为避免丢弃文字或产生不可见 mention，本切片只有在正文为空且没有已选 mention 时进入附件模式；reply 可随附件发送。caption 留给显式兼容扩展，不在本任务顺手开放。
- `假设`：进度口径固定为“source 中已经被 `HttpContent` 读取并复制到请求内容的逻辑文件字节”，不包含 multipart header，不证明 socket 送达或服务端提交。批次进度按 source 声明长度聚合、0–100 单调；稳定 401 的允许重开不会倒退，全部 201 且本地 reservation 成功后才进入 finalizing。
- `假设`：上传前失败保留选择，用户再次点击会启动全新非幂等上传；pending 已提交后的任意结果都清除本次选择并由消息失败行使用原 `ClientMessageId + AttachmentIds` 重试，绝不重新上传。

### 范围

- 必须实现：
  - 无新依赖的本地文件 source 工厂：异步元数据/打开、不阻塞 UI、全有或全无的 1–10 项/重复/名称/大小校验、确定 MIME 分类、可重新打开 seekable `FileStream` 与路径脱敏。
  - 可验证的 Client 内部进度值与 stream 计数包装；进度从 upload transport 贯穿 send coordinator、runtime、shell 到 WPF，回调异常不破坏发送，401 重开、取消、部分批次和 finalizing 边界稳定。
  - shell attachment send 复用当前 Ready selection、reply 目标、规范 payload、认证结束和账户生命周期门；会话切换不取消已经捕获并开始的 flight。
  - WPF 选择/展示/移除、Image/File 模式、上传 ProgressBar、准备/复制/finalizing/完成/失败 live 状态；会话或账户上下文变化清除未发送选择并拒绝旧进度，pending 提交后只清除精确本次输入。
  - 上传前失败与 pending 后失败的不同可恢复文案；后者继续使用既有失败消息行重试且不重新打开本地文件。
  - 覆盖 1/10 项、Unicode、图片/混合/视频/未知扩展、空/超限/重复/删除或长度变化、进度单调/聚合/401/取消/回调异常、selection 切换、认证结束、pending 边界和日志脱敏。
- 允许修改：
  - Client 本地附件 source/progress、Sync/Accounts、`MainWindow.xaml(.cs)`，Client 测试与必要 `docs/ai/` 记录。
- 明确不做：
  - 拖拽、粘贴截图、caption、图片解码/缩略图/查看原图、下载/缓存/进度/打开文件或目录、搜索、服务端进度、断点续传、上传幂等键、跨崩溃草稿恢复、Shared/Server/schema/migration/新依赖、VPS 实测。

### 验收标准

- [ ] 原生多选能生成 1–10 个可重新打开的受控 source；全部受控图片为 Image，混合/视频/未知扩展为 File；无效、重复、空、超限或已变化文件 fail-closed，路径和文件元数据不进入日志/`ToString()`。
- [ ] 当前 Ready 会话可以发送附件并保留合法 reply；正文/mention 非空时不会静默丢弃。会话/账户切换清除旧草稿且旧 progress/result 不污染新上下文，账户结束会取消实际 flight。
- [ ] 批次进度只按真实 source 读取字节推进，0–100 单调且不越界；401 重开不倒退，取消/失败不伪造完成，全部上传经 201 校验并 reservation 成功后才显示 finalizing。
- [ ] 上传前失败保留当前选择供用户显式重新上传；`PendingCommitted=true` 后清除精确选择并只通过既有消息失败行原键 retry，不重传文件。
- [ ] Client 定向测试、Fast、最终一次 Full、WPF Release 启动/响应/精确清理 smoke、Codex reviewer 固定差异、日志脱敏和空白检查通过。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Attachment|FullyQualifiedName~ClientMessageSendCoordinator|FullyQualifiedName~ClientAccountShellCoordinator"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、数据库、非幂等重放边界或 pending 恢复语义，加入新大型依赖，或必须读取/持久化/记录本地绝对路径才能继续。
- 必须把客户端 content-copy 进度表述为服务端提交、在未知上传结果后自动重传，或让附件模式静默丢弃现有正文/mention。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 2.1/9.2–9.4/12.1–12.3/14.1–14.2/阶段 9/21.3、DEC-017/025/035/041/042/043/044/045、STATUS 和本任务。
只做原生文件选择、WPF Image/File 附件模式、客户端 content-copy 进度和既有 durable send 接线；不做拖拽、截图、下载、缩略图、caption、协议/schema/依赖或 VPS。
路径只在内存 closure，进度不冒充服务端提交；未知上传不得自动重传，pending 后只走原键消息 retry。
普通审查使用 Codex reviewer；开发期优先定向/Fast，最终只跑一次 Full，关键失败再扩大重复验证。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 新分支 Fast 基线 | 980/980；Shared 39、Server 255、Client 685、Updater 1；0 警告、0 错误。 |
| `未验证` | 本任务最终门禁 | 实现完成后填写。 |

### 文件范围

- 新增：待完成。
- 修改：待完成。
- 删除：待完成。

### 决策与限制

- 决策：待实现与独立复核后记录。
- 已知限制：拖拽、截图、caption、下载/缩略图/打开和 VPS 留在后续切片。

### 下一步

- 完成本切片后接拖拽与粘贴截图 source 入口，复用本任务的选择和进度链。
