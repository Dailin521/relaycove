# 阶段 9 已下载附件的安全关联应用打开

## 任务定义

- **任务名称：** 阶段 9 受控临时副本、MOTW 与 Windows 关联应用打开
- **状态：** `进行中`
- **基准提交：** `a5a41a41e6622cfc9c35c42d06b0c6090e2a792c`
- **工作分支：** `agent/stage-9-safe-attachment-open`
- **相关方案章节：** 2.1、9.4、14.3–14.5、阶段 9、21.3；`DEC-038/048/049/050`

### 目标

让用户对当前已下载、完整性通过且仍获授权的附件显式选择“打开”，由 Windows Attachment Manager 施加 Restricted Zone 证据、系统安全策略和用户提示后交给关联应用。不得直接执行 opaque `.cache`、不得把任意路径交给 WPF，也不得遗留无界且不受撤权/退出/启动恢复管理的副本。

### 已知事实

- `已验证`：绿色集成基线 `a5a41a4` 已通过最终 Full 1,267/1,267；本任务从已推送的 `agent/v1-integration` 创建，工作区无无关修改。
- `已验证`：现有 reveal 链为 SQLite Downloaded record → 严格 managed path/non-reparse/size/SHA-256 → pinned `ValidatedFile` → immediate SQLite exact-record/current-access confirmation → runtime/Ready selection/UI commit → 无锁 native shell；可复用其授权与 ABA 边界。
- `已验证`：cache final 是 conversation/attachment/full-SHA256 决定的 opaque `.cache`，不能通过系统文件关联直接打开；原名、MIME、URL 和 SQLite 字符串均不能决定 cache 路径。
- `已验证`：Microsoft `IAttachmentExecute` 要求 `Execute` 前设置有效 `LocalPath` 且文件已复制；`Save`/`Execute` 可以运行病毒扫描或其他 trust service、可以写入 `Zone.Identifier`，`Execute` 会在策略要求时显示提示；传出 process handle 时调用方负责关闭。官方文档与本机 Windows SDK 10.0.26100.0 的 IID/CLSID/方法顺序已核对。
- `已验证`：Windows 官方定义 `Zone.Identifier` 为 `[ZoneTransfer]` 文本流，ZoneId 3/4 分别表示 Internet/Restricted。若文件系统、策略或 Attachment Manager 不能保留并复核 ZoneId ≥3，本切片必须 fail closed。
- `已验证`：本决策唯一一次 Claude #78 按 Sonnet/High 发起，但当前任务会话只暴露旧 `consult_claude` 兼容入口，并由该入口强加 `$0.5` 预算后在答案前失败；无 job、正式答案、可靠实际模型、duration 或费用，不追加第二次调用。后续使用 Microsoft 官方契约、本机探针与 Codex 独立复核。

### 已裁定安全边界

- `已验证`：受控打开副本位于 `Path.GetTempPath()` 下的独立 RelayCove/account scope，而不是持久 `%LOCALAPPDATA%`；根、scope 和文件都拒绝 reparse point。只允许严格随机 128-bit basename 与从完整 Windows leaf 验证后提取的 lowercase ASCII terminal extension `[a-z0-9]{1,16}`。无后缀、Unicode/full-width dot、ADS、路径字符、尾点/尾空格和超长后缀 fail closed；不维护会过期的“危险扩展 denylist”，所有语法有效类型统一交给默认 Restricted Zone 和 Windows/企业 Attachment Manager policy。
- `已验证`：副本用 `CreateNew` 从 pinned pathless stream 复制并复算实际 EOF、长度和 SHA-256，写入并回读 Restricted Zone MOTW。因为副本严格位于系统 temp，遵循 Microsoft 临时附件范式不调用 `Save`；专用串行 STA 上只设置固定 client GUID/title 和 `SetLocalPath`，调用 `CheckPolicy`/`Execute`，不设置 `Source`、`Referrer`、`FileName`，不使用 `Process.Start` fallback，不直接执行 `.cache`。
- `已验证`：commit 前注册不可变 open job 与 cleanup lease；最终 DB/access/UI commit 只把已经被 STA worker 接管的 job 置为 committed，commit 后保证恰好一次 `Execute` 尝试，撤权/logout 不取消已提交 job。Windows trust/AV provider 在 `Execute` 中仍可删除或修改文件，这是明确的受信边界；进程强退仍可能发生在 commit 与实际 `Execute` 之间。
- `已验证`：open-copy store 在进程内按账户 scope 跨 runtime generation 共享协调，账户物理总预算不超过 1 GiB并有独立 count reservation、同附件 single-flight 和显式 `StoreFull`；pre-commit 失败立即删除。有 process handle 也只作为机会式清理信号，成功副本至少保留到撤权、注销、正常退出或下次启动扫除；删除失败进入有界重试。已经提交给外部应用的内容无法召回，只能删除仍由 RelayCove 管理的路径。

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

- [ ] 当前已下载附件可从 WPF 显式打开；未下载、损坏、记录变化、撤权、selection/context 变化或扩展结构无效均零关联应用调用且有稳定脱敏状态。
- [ ] 打开副本的路径、扩展名、主数据、MOTW 和预算均由受控 store 验证；Attachment Manager policy/提示不可绕过，病毒扫描删除/修改、无关联和用户取消安全失败。
- [ ] pre-commit 失败不遗留副本；成功副本被撤权/注销/退出/启动恢复清理，锁定删除失败可重试且总磁盘占用有界；跨 runtime generation 不误删仍在 launch 的副本。
- [ ] A→B→A、recycling、并发双击、迟到 completion 和启动中的旧 runtime 不能打开或提交到新 identity；WPF Dispatcher 不做文件 I/O/hash/COM trust 扫描。
- [ ] WPF、UIA、public/internal结果字符串和日志不包含 open/cache path、URL、hash、token、GUID 或原始内部 ID。
- [ ] 定向、Fast、最终 Full、Codex 双路独立复核、真实无害 MOTW/Attachment Manager 探针、model drift、依赖漏洞与空白检查完成。

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
- 副本无法在 1 GiB 物理上限和启动/撤权/退出恢复内收敛，或要求精确追踪外部应用跨进程/跨崩溃生命周期才可工作。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、方案 2.1/9.4/14/阶段 9/21.3、DEC-038/048/049/050、STATUS 和本任务。
只实现已下载附件的受控 open copy + MOTW + IAttachmentExecute；不直接执行 cache，不新增协议/schema/依赖，不绕过 Windows 安全策略。
复用 pinned cache、最终授权和 exact WPF identity；所有外部副作用以明确 commit 线性化并诚实记录不可召回边界。
Claude #78 已失败且不重试；普通实现与最终审查只用 Codex reviewer 和本机证据。
```

## 任务结果

`进行中`。完成实现与验证后填写修改摘要、证据、限制和下一步。
