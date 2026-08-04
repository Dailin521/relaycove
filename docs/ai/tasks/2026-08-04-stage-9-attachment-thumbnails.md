# 阶段 9 本地图片缩略图与有界原图查看

## 任务定义

- **任务名称：** 阶段 9 已下载图片的本地缩略图与有界应用内查看
- **状态：** `进行中`
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
- `已验证`：主代理已按未解决的恶意图片解码 DoS/取消风险启动本决策唯一一次只读 Claude #77（job `abb22632-84bd-4a97-bc74-468cb3751b61`，Opus/XHigh）；子代理不调用 Claude，普通审查由 Codex reviewer 完成。

### 假设

- `假设`：最小闭环可在现有进程内使用 WinRT 内置 decoder、严格格式/尺寸/帧/输出预算和每账户并发上限安全实现；若 #77 或本机压力探针证明进程内解码无法满足 fail-closed 边界，则本任务只冻结安全结论并把隔离 helper 作为独立架构切片，不伪装为已完成。
- `假设`：“查看原图”在 v1 表示读取原始附件后进行有界下采样的应用内预览；超过显示上限时明确标注为受限预览，不承诺按原始像素无限制 materialize。

### 范围

- 必须实现：
  - 只对已下载且仍在当前 Ready exact membership 中的 `image/*` 附件加载；缩略图不触发隐式网络下载。
  - 从 SQLite 下载记录进入 `ValidateAndResolveAsync`，让 decoder 只消费 pinned 内容能力；解码完成后在最终短事务中复核完整下载记录和当前访问权，再提交给当前 UI identity。
  - 仅允许明确识别的内置 PNG/JPEG/GIF/BMP decoder；拒绝签名不符、未知/第三方 codec、损坏、多帧超限、源尺寸/像素或输出预算超限的内容。
  - 缩略图最长边不超过 320；查看预览最长边不超过 4096、输出不超过 16,777,216 像素/64 MiB；所有乘法 checked，复制前验证实际输出，并只向 WPF 交付 frozen `BitmapSource`。
  - 每账户解码并发不超过 2，同一 attachment/rendition single-flight，同时最多一个查看器；切换、撤权、注销、退出、虚拟化卸载和上下文替换取消 flight 并清除强引用。
  - WPF 提供异步缩略图占位、显式键盘可用的“查看图片”、单一有界预览层、关闭/Escape、受限预览提示和脱敏无障碍名称。
  - 自动化覆盖格式/预算/取消、内容替换/撤权/ABA、虚拟化 recycling、viewer 生命周期、UIA 与路径/URL/hash/ID 不泄露。
- 允许修改：Client `Attachments/`、`Storage/`、`Sync/`、`Accounts/`、WPF 与对应 Client 测试；必要的 `docs/ai/` 记录和项目级 Codex 配置。
- 明确不做：自动下载、持久缩略图、`ThumbnailLocalPath` 写入、schema/migration、Shared/Server 协议、服务端缩略图、第三方图像包、文件导出、外部 handler 打开、MOTW/Attachment Manager、VPS/双客户端和真实恶意样本执行。

### 验收标准

- [ ] 已下载的有效图片在可见行异步显示受限缩略图，非图片、未下载、失效或撤权内容不加载且不触发网络。
- [ ] 有界查看器只显示经物理完整性和最终授权复核的 frozen 预览；超大图片下采样并明确标注，超限/损坏/未知 codec 安全失败。
- [ ] A→B→A、snapshot refresh、recycling、下载记录替换、撤权、注销、退出和迟到回调均不能把旧图提交给当前行或重新打开 viewer。
- [ ] 解码并发、single-flight、源/帧/输出预算、checked 计算、取消和强引用清理均有自动化证据；WPF Dispatcher 不执行文件 I/O、hash 或 decode。
- [ ] presentation、`ToString()`、UIA、日志和 public result 不包含 cache 路径、relative path、URL、hash 或内部 ID。
- [ ] 定向、Fast、最终 Full、Codex 独立复核、唯一一次 Claude #77 答案读取与本地裁定、model drift、依赖漏洞和空白检查完成。

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
- 本机证据表明 decoder 取消或预算不能形成可接受的进程内边界，且没有当前范围内的小型 fail-closed 修复。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、方案 9.4/14.4/阶段 9、DEC-034/044/048/049、STATUS 和本任务。
只实现已下载图片的内存缩略图与应用内有界预览；不写 ThumbnailLocalPath，不新增协议/schema/依赖，不隐式下载。
复用 pinned cache 完整性与最终授权边界，并对 UI identity、并发、输出内存、取消和虚拟化生命周期 fail-closed。
Claude #77 只由主代理读取一次；普通实现审查只用 Codex reviewer。
```

## 任务结果

`进行中`。完成实现与验证后填写修改摘要、证据、限制和下一步。
