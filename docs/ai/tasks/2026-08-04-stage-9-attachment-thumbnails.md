# 阶段 9 本地图片缩略图与有界原图查看

## 任务定义

- **任务名称：** 阶段 9 已下载图片的本地缩略图与有界应用内查看
- **状态：** `已完成`
- **基准提交：** `acc9929831bfd6d3d0f3a39e5a7cc911b90d1958`
- **工作分支：** `agent/stage-9-attachment-thumbnails`
- **相关方案章节：** 9.4、14.4、阶段 9、21.3；`DEC-034/044/048/049`

### 目标

只对当前已下载、已确认且仍获授权的 Image 附件异步生成内存缩略图，并提供一个应用内、有明确像素和内存上限的原图预览。解码结果不得绕过受控 cache 完整性与最终授权复核，也不得把路径、URL、hash 或内部 ID 暴露给 WPF、日志或自动化字段。

### 已知事实

- `已验证`：集成基线 `acc9929` 已通过最终 Full 1,211/1,211、附件定向 338/338和真实 Explorer 精确选中；本任务从已合入的绿色 integration 头创建。
- `已验证`：工程方案要求缩略图异步生成，大图不得无限制加载；v1 不要求服务端缩略图服务。
- `已验证`：现有 cache 以账户/会话隔离的 opaque `.cache` 保存内容，`ValidateAndResolveAsync` 会复核受控路径、非 reparse、长度和完整 SHA-256，并以只读句柄固定已验证内容。
- `已验证`：`ThumbnailLocalPath` 虽存在于 SQLite schema，但当前恢复、配额、撤权与物理枚举均不管理衍生文件；本切片不写该列、不生成衍生文件。
- `已验证`：Windows SDK 的 `Windows.Graphics.Imaging.BitmapDecoder`、`BitmapTransform`、`SoftwareBitmap.CopyToBuffer` 和取消桥接已由当前项目引用提供，无需新增包。
- `已验证`：主代理已按未解决的恶意图片解码 DoS/取消风险完成本决策唯一一次只读 Claude #77（job `abb22632-84bd-4a97-bc74-468cb3751b61`，实际 `claude-opus-5`，Opus/XHigh，model mismatch=false，795.273 秒，精确成本 `$3.05042375`）；子代理不调用 Claude，普通审查由 Codex reviewer 完成。

### 假设

- `假设`：最小闭环可在现有进程内使用 WinRT 内置 decoder、严格格式/尺寸/输出预算、超时脱离和每账户并发上限安全实现；最终结论仍以本机自动化、压力探针与 Codex 独立复核为准。
- `假设`：“查看原图”在 v1 表示读取原始附件后进行有界下采样的应用内预览；超过显示上限时明确标注为受限预览，不承诺按原始像素无限制 materialize。

### Claude #77 本地裁定

- `已采纳`：仅允许 Windows 内置 JPEG/PNG codec；GIF/BMP 和未知/第三方 codec fail closed。输入上限 25 MiB，源像素上限 16,777,216、单边上限 16,384，查看预览最长边 2,560；PNG 另限制源像素不得超过输入字节的 256 倍。
- `已采纳`：验证并固定 cache 内容后，在 pinned 句柄内复制到私有有界内存，立即释放磁盘句柄，再向 decoder 交付无路径 `MemoryStream`；迟到或不响应取消的 decoder 不再阻塞撤权后的物理 cache 清除。
- `已采纳`：每账户最多两个解码 slot；单次解码和等待 slot 均为 10 秒。超时任务脱离 UI 等待但继续占用 slot，直至实际返回后才清零输入并释放，防止连续超时绕过并发上限。
- `本地保留`：当前 Windows 目标与真实解码测试已验证 WinRT API 可编译和解码，且 `CodecId` 可与内置 JPEG/PNG ID 精确比对，因此本切片不改用 WIC，也不新增原生依赖。
- `暂不采纳`：持久/跨行 LRU 不属于安全正确性前提；缩略图在虚拟化卸载、上下文替换、选择切换、注销和退出时清引用。若后续性能证据显示重复解码不可接受，再单独设计有总字节预算且撤权可清空的 LRU。
- `重新评估 helper 的触发条件`：任何 codec 进程崩溃、两个 slot 被长期占满、内存预算在压力测试中不可接受、扩展白名单格式或引入第三方 codec。触发后改为独立低权限解码进程，不在本切片假装已解决。

### 范围

- 必须实现：
  - 只对已下载且仍在当前 Ready exact membership 中的 `image/*` 附件加载；缩略图不触发隐式网络下载。
  - 从 SQLite 下载记录进入 `ValidateAndResolveAsync`，在 pinned 内容能力内复制到私有有界内存后释放磁盘句柄；解码完成后在最终短事务中复核完整下载记录和当前访问权，再提交给当前 UI identity。
  - 仅允许签名和 `CodecId` 均匹配的 Windows 内置 PNG/JPEG decoder；拒绝 GIF/BMP、签名不符、未知/第三方 codec、损坏、源尺寸/像素、PNG 压缩倍率或输出预算超限的内容。
  - 输入不超过 25 MiB，源不超过 16,777,216 像素/单边 16,384；缩略图最长边不超过 320，查看预览最长边不超过 2,560，输出不超过 6,553,600 像素/25 MiB；所有乘法 checked，复制前验证实际输出，并只向 WPF 交付 frozen `BitmapSource`。
  - 每账户解码并发不超过 2，等待 slot 和实际解码分别最多 10 秒，同一 attachment/rendition single-flight，同时最多一个查看器；切换、撤权、注销、退出、虚拟化卸载和上下文替换取消 flight 并清除 UI 强引用。
  - WPF 提供异步缩略图占位、显式键盘可用的“查看图片”、单一有界预览层、关闭/Escape、受限预览提示和脱敏无障碍名称。
  - 自动化覆盖格式/预算/取消/超时脱离、内容替换/撤权/ABA、虚拟化 recycling、viewer 生命周期、UIA 与路径/URL/hash/ID 不泄露。
- 允许修改：Client `Attachments/`、`Storage/`、`Sync/`、`Accounts/`、WPF 与对应 Client 测试；必要的 `docs/ai/` 记录和项目级 Codex 配置。
- 明确不做：自动下载、持久缩略图、`ThumbnailLocalPath` 写入、schema/migration、Shared/Server 协议、服务端缩略图、第三方图像包、文件导出、外部 handler 打开、MOTW/Attachment Manager、VPS/双客户端和真实恶意样本执行。

### 验收标准

- [x] 已下载的有效图片在可见行异步显示受限缩略图，非图片、未下载、失效或撤权内容不加载且不触发网络。
- [x] 有界查看器只显示经物理完整性和最终授权复核的 frozen 预览；超大图片下采样并明确标注，超限/损坏/未知 codec 安全失败。
- [x] A→B→A、snapshot refresh、recycling、下载记录替换、撤权、注销、退出和迟到回调均不能把旧图提交给当前行或重新打开 viewer。
- [x] 解码并发、single-flight、源/压缩倍率/输出预算、checked 计算、取消、超时脱离和强引用清理均有自动化证据；WPF Dispatcher 不执行文件 I/O、hash 或 decode。
- [x] presentation、`ToString()`、UIA、日志和 public result 不包含 cache 路径、relative path、URL、hash 或内部 ID。
- [x] 定向、Fast、最终 Full、Codex 独立复核、唯一一次 Claude #77 答案读取与本地裁定、model drift、依赖漏洞和空白检查完成。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~AttachmentImage|FullyQualifiedName~AttachmentDownload|FullyQualifiedName~MessageListPresenter|FullyQualifiedName~AccountShell"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须新增大型/原生第三方 decoder、helper 进程、schema/protocol 或持久衍生缓存才能满足验收，或者已验证图片能力必须向 WPF 暴露路径。
- 本机证据表明 decoder 取消、超时脱离或预算不能形成可接受的进程内边界，且没有当前范围内的小型 fail-closed 修复。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、方案 9.4/14.4/阶段 9、DEC-034/044/048/049、STATUS 和本任务。
只实现已下载图片的内存缩略图与应用内有界预览；不写 ThumbnailLocalPath，不新增协议/schema/依赖，不隐式下载。
复用 pinned cache 完整性与最终授权边界，并对 UI identity、并发、输出内存、取消和虚拟化生命周期 fail-closed。
Claude #77 只由主代理读取一次；普通实现审查只用 Codex reviewer。
```

## 任务结果

`已完成`。生产代码提交为 `fabe16c28476237a1ed9d91f26a6738d09057c0f`。

### 修改摘要

- cache 只在验证句柄固定期间把不超过 25 MiB 的精确内容复制到无路径私有内存；磁盘能力随后立即释放，解码完成后清零输入，最终提交前再次复核完整下载记录、当前授权和 exact UI identity。
- 新增 Windows 内置 PNG/JPEG 白名单解码器、源尺寸/像素、PNG 压缩倍率、输出像素/内存和 checked 算术边界；缩略图最长边 320、查看器最长边 2,560，WPF 只接收 frozen `BitmapSource`。
- 进程内同账户数据库 scope 跨 runtime generation 共享两个 slot 与 attachment/rendition single-flight；等待和解码各 10 秒。超时 decoder 脱离 UI，但在实际返回前继续持有 slot 与输入；脱离任务的关键进程异常在完成清理后交给 production fail-fast 边界。
- WPF 只为当前 Ready、已下载的可见图片行加载缩略图，不触发网络；提供单一模态查看器、Escape/关闭、状态提示、UIA 脱敏、recycling/选择/撤权/注销清理，并修复新 snapshot materialize 时序和自动关闭后的键盘焦点回退。

### 验证证据

- 相关 `AttachmentImage|AttachmentDownload|MessageListPresenter|AccountShell` 回归最终 `218/218`；真实 PNG/JPEG、压缩倍率/尺寸预算、跨 coordinator owner、critical detached cleanup、A→B→A、recycling、viewer 和焦点时序均有自动化覆盖。
- 最终 `pwsh ./scripts/verify.ps1 -Mode Full` 通过：Release 构建 0 警告/0 错误，`1,267/1,267`（Shared 39、Server 255、Client 972、Updater 1），format 与 `git diff --check` 通过。首次 Full 只在五处机械格式门禁停止，使用仓库 formatter 修复后最终复跑全绿。
- EF `has-pending-model-changes` 返回无模型变化；八个项目的 direct/transitive 漏洞扫描均未发现已知漏洞。
- 两路 Codex 独立复审提出的跨 generation owner 清理、critical cleanup 顺序、WPF snapshot materialize 和自动焦点问题均已修复并补回归。Claude #77 已读取并逐项本地裁定：job `abb22632-84bd-4a97-bc74-468cb3751b61`，实际 `claude-opus-5`、mismatch=false、795,273 ms、精确成本 `$3.05042375`。

### 已知限制与下一步

- 永久不返回的进程内 decoder 会按设计继续占用一个账户 slot 和最多 25 MiB 输入，直至进程退出；静态账户 scope 不主动驱逐。若真实压力出现 codec 崩溃、两个 slot 长期占满、内存不可接受或需要扩展白名单，则触发独立低权限 helper 进程重新设计。
- 本切片不生成持久缩略图、不隐式下载、不写 `ThumbnailLocalPath`，也不开放外部程序直接打开；真实登录图片视觉、Narrator、恶意样本、VPS 与双客户端仍保留到后续/M5 Gate。
- 下一切片单独裁定并实现直接打开文件的可信 handler、扩展名、MOTW/Attachment Manager 与临时副本/撤权生命周期。
