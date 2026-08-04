# 阶段 9 已下载附件的安全关联应用打开

## 任务定义

- **任务名称：** 阶段 9 受控临时副本、MOTW 与 Windows 关联应用打开
- **状态：** `已完成`
- **基准提交：** `a5a41a41e6622cfc9c35c42d06b0c6090e2a792c`
- **工作分支：** `agent/stage-9-safe-attachment-open`
- **已提交安全边界：** `4c373892e69dbadce856c2af132a412d99d031c8`（受控 open-copy store、MOTW 与 `IAttachmentExecute` STA 边界）
- **生产代码提交：** `f8c3dcd22c40ec511665314b5daf07a58a798a9d`
- **相关方案章节：** 2.1、9.4、14.3–14.5、阶段 9、21.3；`DEC-038/048/049/050/051`

### 目标

让用户对当前已下载、完整性通过且仍获授权的附件显式选择“打开”，由 Windows Attachment Manager 施加 Restricted Zone 证据、系统安全策略和用户提示后交给关联应用。不得直接执行 opaque `.cache`、不得把任意路径交给 WPF，也不得遗留无界且不受撤权/退出/启动恢复管理的副本。

### 已知事实

- `已验证`：绿色集成基线 `a5a41a4` 已通过最终 Full 1,267/1,267；本任务从已推送的 `agent/v1-integration` 创建。该 Full 发生在本切片实现前，不能作为本切片的最终验证。
- `已验证`：现有 reveal 链为 SQLite Downloaded record → 严格 managed path/non-reparse/size/SHA-256 → pinned `ValidatedFile` → immediate SQLite exact-record/current-access confirmation → runtime/Ready selection/UI commit → 无锁 native shell；可复用其授权与 ABA 边界。
- `已验证`：cache final 是 conversation/attachment/full-SHA256 决定的 opaque `.cache`，不能通过系统文件关联直接打开；原名、MIME、URL 和 SQLite 字符串均不能决定 cache 路径。
- `已验证`：Microsoft `IAttachmentExecute` 要求 `Execute` 前设置有效 `LocalPath` 且文件已复制；`Save`/`Execute` 可以运行病毒扫描或其他 trust service、可以写入 `Zone.Identifier`，`Execute` 会在策略要求时显示提示；传出 process handle 时调用方负责关闭。官方文档与本机 Windows SDK 10.0.26100.0 的 IID/CLSID/方法顺序已核对。
- `已验证`：Windows 官方定义 `Zone.Identifier` 为 `[ZoneTransfer]` 文本流，ZoneId 3/4 分别表示 Internet/Restricted。若文件系统、策略或 Attachment Manager 不能保留并复核 ZoneId ≥3，本切片必须 fail closed。
- `已验证`：本决策唯一一次 Claude #78 按 Sonnet/High 发起，但当前任务会话只暴露旧 `consult_claude` 兼容入口，并由该入口强加 `$0.5` 预算后在答案前失败；无 job、正式答案、可靠实际模型、duration 或费用，不追加第二次调用。后续使用 Microsoft 官方契约、本机探针与 Codex 独立复核。

### 已裁定安全边界

- `已验证`：受控打开副本位于 `Path.GetTempPath()` 下的独立 RelayCove/account scope，而不是持久 `%LOCALAPPDATA%`；根、scope 和文件都拒绝 reparse point。只允许严格随机 128-bit basename 与从完整 Windows leaf 验证后提取的 terminal extension：原始字符必须是 1–16 位 ASCII 字母或数字，之后才规范为 lowercase `[a-z0-9]{1,16}`。无后缀、Unicode/full-width dot、ADS、路径字符、尾点/尾空格和超长后缀 fail closed；不维护会过期的“危险扩展 denylist”，所有语法有效类型统一交给默认 Restricted Zone 和 Windows/企业 Attachment Manager policy。
- `已验证`：副本用 `CreateNew` 从 pinned pathless stream 复制并复算实际 EOF、长度和 SHA-256，写入并回读 Restricted Zone MOTW。因为副本严格位于系统 temp，遵循 Microsoft 临时附件范式不调用 `Save`；专用串行 STA 上只设置固定 client GUID/title 和 `SetLocalPath`，调用 `CheckPolicy`/`Execute`，不设置 `Source`、`Referrer`、`FileName`，不使用 `Process.Start` fallback，不直接执行 `.cache`。
- `已验证`：commit 前注册不可变 open job 与 cleanup lease。Prepare 完成 COM 配置/policy；最终 DB/access/UI commit 只把已接管 job 置为 committed 并等待 foreground acknowledgement，不调用 `Execute`；整个 `ConfirmDownloadedAttachmentAsync` 返回或抛出，且 SQLite transaction、local-cache operation gate、coordinator `CommitGate`、shell `stateGate` 都已退出后才 `ExecuteRelease`，随后恰好一次 `Execute`。撤权/logout 不取消已提交 job；Windows trust/AV provider 在 `Execute` 中仍可删除或修改文件，进程强退仍可能发生在 commit 与实际 `Execute` 之间。
- `已验证`：open-copy store 在进程内按账户 scope 跨 runtime generation 共享协调；预算是每账户 open-copy **主数据流的逻辑字节**最多 1 GiB，另有最多 64 个文件的 reservation，而非 ADS 或实际磁盘分配的物理上限。pre-commit 失败立即删除。`Execute` 返回的 process handle 仅按所有权立即关闭，不作 cleanup 或查看器生命周期信号。成功副本至少保留到撤权、注销、正常退出或下次启动扫除；删除失败保留为 pending，并在后续 revoke/logout/dispose/recovery cleanup 触发时重试，不声称后台任务或重试次数有界。已经提交给外部应用的内容无法召回，只能删除仍由 RelayCove 管理的路径。

### 范围

- 必须实现：
  - WPF 下载完成态同时提供“打开”和“在文件夹中显示”；两者都只传 attachment identity，支持键盘/UIA，并有独立 exact operation/selection/recycling/A→B→A 门。
  - 新建账户隔离 open-copy store，严格根/文件名/non-reparse/ADS/配额/并发与 startup orphan recovery；副本不使用原始 basename，不进入 SQLite，不改变 cache quota/命名。
  - 对完整 Windows leaf 和 terminal extension 做确定性规范化；所有语法安全类型交由默认 Restricted Zone Attachment Manager/企业策略，缺少关联时稳定失败，不以本地 denylist 冒充长期安全边界。
  - 使用应用生命周期拥有的专用串行 STA worker 调用 Windows `IAttachmentExecute`：固定 client GUID/title、`SetLocalPath`、`CheckPolicy`、`Execute`；不调用 `Save`，不设置 source/referrer/file name，不传 access token、原 download URL 或任意 verb/arguments/working directory，不提供绕过安全策略的 fallback。
  - 复用物理 cache 校验、最终 SQLite 授权和 shell/UI commit；撤权、注销、退出、runtime 重建、取消与迟到回调清理或挂起重试所有受控副本。
  - 自动化覆盖扩展名混淆/危险类型、路径/ADS/reparse、MOTW、精确 copy/hash、配额、startup/revoke/logout cleanup、DB/授权/ABA、COM 调用顺序/策略/用户取消/无 handler、脱敏与无网络；本机只用无害文本做 Attachment Manager/MOTW 探针。
- 允许修改：Client `Attachments/`、`Storage/`、`Sync/`、`Accounts/`、WPF、Client 测试和必要的 `docs/ai/` 记录。
- 明确不做：自动下载、导出/另存为、编辑后回传、硬链接、原名持久副本、直接运行 `.cache`、绕过 Attachment Manager、修改 Shared/Server/SQLite schema、第三方包、搜索/管理员/更新，以及 VPS/双客户端 Gate。

### 验收标准

- [x] 当前已下载附件可从 WPF 显式打开；未下载、损坏、记录变化、撤权、selection/context 变化或扩展结构无效均零关联应用调用且有稳定脱敏状态。
- [x] 打开副本的路径、扩展名、主数据、MOTW 和预算均由受控 store 验证；Attachment Manager policy/提示不可绕过，病毒扫描删除/修改、无关联和用户取消安全失败。
- [x] 三阶段协议保证 blocked post-authorize tail 时 `ExecuteCount == 0`；确认调用解开全部 gate 后才 release 并恰好执行一次。tail 的 throw/non-ready/cancellation/disposal 映射真实 Windows 结果，foreground STA 覆盖等待 release 到 COM 收敛，`DisposeAsync` 只等 Execute-entry-or-terminal。
- [x] pre-commit 失败不遗留副本；成功副本被撤权/注销/退出/启动恢复清理，锁定删除失败保留 pending 并可在后续 cleanup 触发时重试；每账户主数据流逻辑字节预算为 1 GiB、文件数为 64，跨 runtime generation 不误删仍在 launch 的副本。
- [x] A→B→A、recycling、并发双击、迟到 completion 和启动中的旧 runtime 不能打开或提交到新 identity；WPF Dispatcher 不做文件 I/O/hash/COM trust 扫描。
- [x] WPF、UIA、public/internal结果字符串和日志不包含 open/cache path、URL、hash、token、GUID 或原始内部 ID。
- [x] 定向、Fast、最终 Full、Codex 双路独立复核、真实无害 MOTW/Attachment Manager 探针、model drift、依赖漏洞与空白检查完成。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~AttachmentOpen|FullyQualifiedName~AttachmentDownload|FullyQualifiedName~MessageListPresenter|FullyQualifiedName~AccountShell"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须直接执行 `.cache`、使用硬链接、把任意路径/原名/URL/token 交给 WPF 或无策略 shell，或者必须新增协议/schema/大型依赖才能满足打开功能。
- 本机证据证明 `IAttachmentExecute` 在受控 STA/owner HWND、MOTW、扫描、提示或关联处理上不能形成 fail-closed 边界，且当前范围内没有小型修复；不得回退到裸 `Process.Start`。
- 副本无法在每账户 1 GiB 主数据流逻辑字节、64 文件预算及启动/撤权/退出恢复内收敛，或要求精确追踪外部应用跨进程/跨崩溃生命周期才可工作。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、方案 2.1/9.4/14/阶段 9/21.3、DEC-038/048/049/050、STATUS 和本任务。
只实现已下载附件的受控 open copy + MOTW + IAttachmentExecute；不直接执行 cache，不新增协议/schema/依赖，不绕过 Windows 安全策略。
复用 pinned cache、最终授权和 exact WPF identity；用 Prepare→commit/foreground acknowledgement→全部 gate 退出后的 ExecuteRelease 线性化外部副作用，并诚实记录不可召回边界。
Claude #78 已失败且不重试；普通实现与最终审查只用 Codex reviewer 和本机证据。
```

## 任务结果

`已完成`。安全打开纵向切片的基础边界为
`4c373892e69dbadce856c2af132a412d99d031c8`，最终 production 代码为
`f8c3dcd22c40ec511665314b5daf07a58a798a9d`；账户组合、协调器、WPF 双动作、
三阶段执行协议、清理生命周期与回归证据均已收敛。

### 修改摘要

- `已验证`：打开只接受当前已下载记录。协调器先对 cache 执行严格 managed-path、
  non-reparse、长度和 SHA-256 验证；随后 open-copy store 以 `CreateNew` 从 pinned、
  pathless source 复制并复核 EOF、长度和 SHA-256。
- `已验证`：副本位于系统 temp 下账户隔离的受控目录，使用随机 128-bit basename；
  原始 Windows leaf 必须为单一、合法 leaf，终端扩展名规范为 lowercase ASCII
  `[a-z0-9]{1,16}`。路径、ADS、reparse point、尾点/尾空格、Unicode/full-width dot、
  无后缀和超长/无效扩展均 fail closed；不使用危险扩展 denylist。
- `已验证`：副本写入并回读 `[ZoneTransfer]`/`ZoneId=4` 的 `Zone.Identifier`；无法保留
  或复核 MOTW 时不打开。open-copy store 以每账户 1 GiB 主数据流逻辑字节预算、最多
  64 项的独立文件 reservation 和同附件 single-flight 约束副本；该预算不声称覆盖 ADS
  或实际磁盘分配，失败在 commit 前删除。
- `已验证`：应用生命周期拥有一个串行 STA worker。它只设置固定 client GUID/title 与
  `SetLocalPath`，然后执行 `CheckPolicy` 和一次 `Execute`；不调用 `Save`、不设置
  `Source`/`Referrer`/`FileName`，不暴露 URL、token、verb、arguments 或 working
  directory，也没有 `Process.Start` 或直接 `.cache` 的回退。
- `已验证`：精确 cleanup 生命周期为：STA 已接管的 job 在最终 SQLite downloaded-record/
  当前 access 确认及账户壳 exact selection commit 后才被标记 committed，并先返回
  foreground acknowledgement；此时 worker 仍等待 `ExecuteRelease`。只有整个确认调用
  已返回或抛出、SQLite transaction、local-cache operation gate、coordinator `CommitGate`
  与 shell `stateGate` 全部退出后才放行，Windows 随后获得恰好一次 `Execute` 尝试。
  撤权或 logout 不再取消该尝试。只有在
  `Execute` 返回、COM 已 Release 且 job completion 已发布后，才调用
  `CompleteLaunchAsync` 并允许清理；返回的 process handle 仅按所有权立即 Close。
  撤权、logout、正常退出、runtime 重建和 startup orphan recovery 均请求已提交副本
  清理，锁定/失败路径保留为 pending，并在后续 revoke/logout/dispose/recovery cleanup
  触发时重试；不声称后台重试或次数有界。正常关闭时，committed STA job 会从
  acknowledgement 起保持前台，覆盖等待 release、`Execute`、handle/COM release、
  completion 和 retire；`DisposeAsync` 本身只等待 Execute-entry-or-terminal，不等待
  `Execute` 返回。强制终止仍可能发生在 commit 与实际 `Execute` 之间，
  已交给外部应用的内容不可召回。
- `已验证`：WPF 下载完成态保留“在文件夹中显示”并新增独立“打开”动作。两者均只传
  attachment identity；打开动作有独立 operation、selection、recycling 与 A→B→A 门，
  Dispatcher 不执行文件 I/O、hash 或 COM trust 扫描。
- `已验证`：打开结果为 pathless、identity-free 的状态映射：成功为
  `HandedToWindows`；并发为 `InProgress`；策略拒绝、用户取消、无关联、本地校验失败、
  空间不足、撤权、陈旧上下文和取消均以稳定脱敏 UI 文案返回。路径、URL、hash、token、
  GUID 和内部 ID 不跨越到 WPF/UIA 或公开结果字符串。

### 当前验证证据

- `已验证（较早修复前组合）`：本切片相关定向测试通过 `47/47`；更广的附件/账户筛选
  测试通过 `241/241`。
- `已验证（最终修复前的最新组合）`：定向测试 `250/250`、无害文本真实
  Attachment Manager/MOTW 探针 `1/1`、Fast/Full `1,327/1,327`、format、
  `git diff --check`、model drift 与依赖漏洞审计均通过。
- `已验证`：实现前 Fast 基线为 `1,267/1,267`；它仅证明起始检查点，不能证明当前
  实现。
- `已验证（最终代码）`：production `f8c3dcd22c40ec511665314b5daf07a58a798a9d`；
  相关定向 `271/271`，Fast 与 Full 各 `1,348/1,348`（Shared 39、Server 255、Client
  1,053、Updater 1），Debug/Release 均 0 警告、0 错误；真实 Windows 无害文本
  MOTW/Attachment Manager 探针 `1/1`，Release `RelayCove` 主窗口已创建且响应。
- `已验证`：model drift 无变化；八项目传递依赖漏洞审计无漏洞；format、
  `git diff --check` 通过。两路 Codex reviewer 对最新三阶段工作树均给出 PASS，无
  P0/P1/P2；blocked/throw post-authorize tail、foreground/Dispose、COM Release 后 cleanup、
  A→B→A、WPF 14 状态与日志/UIA 脱敏均在最终回归中覆盖。

### 限制与下一步

- `已验证`：Windows trust/AV provider 可在 `Execute` 内删除或修改副本；这是由
  Attachment Manager 承担的受信边界。缺少关联应用、策略拒绝和用户取消均安全失败。
- `未验证`：真实登录视觉、恶意样本、VPS 与双客户端 Gate 仍不在本切片内。
- 下一步：仅快进合入 `agent/v1-integration`，关闭 M2，进入 M3 搜索纵向切片。
