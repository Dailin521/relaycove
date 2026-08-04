# 阶段 9 WPF 附件拖拽与粘贴截图

## 任务定义

- **任务名称：** 阶段 9 WPF 文件拖拽与有界内存截图粘贴入口
- **状态：** `已完成`
- **基准提交：** `8ac80fb9b70953f6ff2e56d38b6278f2f765e2b7`
- **工作分支：** `agent/stage-9-wpf-attachment-inputs`
- **相关方案章节：** 2.1、14.1–14.2、阶段 9、21.3；`DEC-042/045/046/047`

### 目标

让当前 Ready 会话在正文为空且没有 mention 时，可以把 Windows `FileDrop` 文件拖入现有附件草稿，也可以在 composer 输入区用 Ctrl+V 把剪贴板位图异步编码为 PNG 草稿；两种入口复用现有 1–10 项、exact context/draft、content-copy progress 和 durable pending/no-replay 链。

### 已知事实

- `已验证`：绿色集成头、本分支基线与远端 `agent/v1-integration` 都是 `8ac80fb9b70953f6ff2e56d38b6278f2f765e2b7`；新分支 Fast 1045/1045（Shared 39、Server 255、Client 750、Updater 1），Debug 构建 0 警告、0 错误。
- `已验证`：现有文件 factory 已提供原子 1–10 项、路径去重、名称/大小/可读性、可重开 stream 与路径脱敏；composer 已提供 exact conversation/context/draft 门、PendingCommitted 清理和上传进度。
- `已验证`：`ClientAttachmentUploadSource` 只要求每次返回可读、seekable、精确剩余长度的 stream，不要求磁盘路径；冻结的 PNG 字节可以通过每次新建只读 `MemoryStream` 满足 401 reopen，无需临时文件或 schema。
- `已验证`：Client 是 Windows WPF TFM 且无新依赖即可使用 `FileDrop`、`Clipboard.GetImage`、`BitmapSource` 和 `PngBitmapEncoder`；当前仓库没有拖拽或位图剪贴板读取实现。
- `已验证`：Claude Code 2.1.221 关键只读 challenge 已以后台任务 `c3e1ce54` 完成；实际 `claude-opus-5`、工作区 `E:\WorkSpace\RelayCove`、工具限于 Read/Glob/Grep。成立的内存、物化、文本优先、取消、WIC 分类与 buffer 边界均由 Codex 复算、修正并本机复验。

### 冻结口径

- `已验证`：拖拽只接受真实 `DataFormats.FileDrop` 且源允许 Copy，不接受 shell 虚拟文件、URL、文本、目录或自动转换；外部读取前后执行 exact context 门，路径快照之后完全复用文件 factory，全批成功或全批失败。
- `已验证`：截图入口只接受 composer 输入区的 exact Ctrl+V；若剪贴板同时声明文本与位图，优先保留 TextBox 默认文本粘贴，键盘 repeat 不重复读取图片。读取和 `CachedBitmap(OnLoad)` BGRA32 快照在 UI STA 完成，冻结后才在后台编码 PNG。
- `已验证`：单个 PNG 与全部内存截图草稿 retained buffer 合计均不超过 25 MiB，与服务端默认上传限制一致；原始 BGRA32 快照不超过 100 MiB。最终 source 使用精确长度、只读且不可公开底层数组的 buffer；单飞 owner 与上下文取消阻止多个大图编码并发放大。
- `已验证`：同一截图重复粘贴是独立显式草稿，不按内容 hash 去重；固定展示名 `clipboard-image.png` 不携带时间戳或 GUID。截图内容、尺寸、hash、编码字节和内存 identity 不进入日志、错误、SQLite、网络额外字段或 `ToString()`。

### 范围

- 必须实现：
  - source-neutral 的附件 draft 内部模型，使文件路径草稿与纯内存 PNG 草稿共享展示、发送和 exact draft identity；文件路径去重只比较文件草稿。
  - 无新依赖的冻结位图 → 后台有界 PNG 编码 → 可重开只读内存 source；取消、像素/输出/累计内存上限、编码异常和脱敏结果可验证。
  - 只读 Clipboard 位图适配器，稳定分类无图片、暂时占用、无效图片；不读取或覆盖真实系统 Clipboard 的自动化。
  - composer 输入区的 FileDrop Copy/None 效果、高亮、Drop 路径快照与 Ctrl+V 图片入口；保留“附件”按钮作为键盘/辅助技术等价入口。
  - 两种入口均复用 empty text/no mention、1–10、reply、A→B→A context/draft、pending/no-replay 与状态提示；异步失败不添加部分草稿。
- 允许修改：
  - Client `Attachments/`、`MainWindow.xaml(.cs)`、对应 Client 测试与必要 `docs/ai/` 记录。
- 明确不做：
  - 粘贴文件、shell 虚拟文件、URL/文本拖拽、caption、图片缩略图/查看原图、下载/缓存/下载进度、打开文件/目录、跨崩溃草稿、Shared/Server/schema/migration/依赖、VPS。

### 验收标准

- [x] FileDrop 只在当前可接受 composer 状态显示 Copy；有效文件与按钮选择行为一致，目录/虚拟/无效/超限/重复批次原子拒绝且不显示路径。
- [x] Ctrl+V 图片在 UI STA 取得快照后后台编码为规范 PNG，source 可至少重开两次且内容一致；无图不截获文本粘贴，占用/无效/取消/像素或编码上限均 fail-closed。
- [x] 普通文件与截图合计最多 10 项；全部图片仍为 Image，混合文件为 File；重复截图是独立显式草稿，路径文件仍跨批去重。
- [x] 拖拽/截图 await 期间的会话切换、A→B→A、草稿变化或账户结束不会添加旧结果；pending 前失败保留 exact 草稿，pending 后沿用 `DEC-045/046` 原键 retry 且不重编码/重传。
- [x] 定向、Fast、最终一次 Full、真实 Release WPF lifecycle/可行的 Drop+clipboard smoke、Codex reviewer、关键 Claude challenge 记录、日志脱敏和空白检查完成。

### 验证命令

```powershell
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~ClientAttachment|FullyQualifiedName~Clipboard"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
git diff --check
```

### 停止并询问

- 必须把截图写入持久磁盘/SQLite、记录内容或路径、增加依赖、改变上传/pending/no-replay 协议，或必须读取非图片剪贴板内容才能继续。
- 有界内存方案无法在不引入不可接受 OOM/线程亲和风险下成立，或必须加入显著改变产品体验的新截图尺寸/数量限制。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 2.1/14.1–14.2/阶段9/21.3、DEC-045/046、STATUS 和本任务。
只做 FileDrop 文件与 Ctrl+V 位图 PNG 两个输入适配器，复用现有 draft/context/progress/pending 链。
位图先在 UI STA 冻结为受控 BGRA32，再后台写入有界内存；不落临时文件、不读取非图片剪贴板、不记录路径或内容。
普通审查由 Codex reviewer；Claude 只对截图内存/隐私边界做一次关键只读 challenge。
```

## 任务结果

### 修改摘要

- 以 source-neutral `ClientAttachmentDraft` 统一磁盘文件与纯内存 PNG 草稿；文件仍保留路径 identity 去重，截图只保留精确 retained byte 预算和可重开 source。
- 新增 exact `FileDrop` Copy/None 策略、路径快照及 WPF drop target；外部数据读取前后校验 conversation/context/draft，继续复用既有原子文件工厂与 durable send 链。
- 新增文本优先的 exact Ctrl+V 读取、STA `CachedBitmap(OnLoad)` 物化、后台有界 PNG 编码、25/100 MiB 双预算、精确私有 buffer、WIC 取消/超限分类和单飞生命周期取消。
- 真正退出、账户/会话/草稿变化都会取消当前输入，但 owner 保持 busy 到原操作 finally，避免取消中的 WIC 与新大图编码重叠。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 新分支 Fast 基线 | 1045/1045；Shared 39、Server 255、Client 750、Updater 1；0 警告、0 错误。 |
| `已验证` | 附件/Clipboard Debug 定向 | 132/132；覆盖 exact Ctrl+V、文本优先/repeat、FileDrop exact format/Copy、双预算、PNG 重开、源变更物化、取消、WIC 包装、私有精确 buffer、路径/结果脱敏与 source-neutral draft。 |
| `已验证` | 最终 Fast | 1088/1088；Shared 39、Server 255、Client 793、Updater 1；Debug 构建 0 警告、0 错误。 |
| `已验证` | 最终一次 Full | 1088/1088；Release 构建 0 警告、0 错误；format verify 与 `git diff --check` 通过。 |
| `已验证` | 两路 Codex reviewer 最终复核 | 一路无 P0/P1/P2/P3；安全复核无 P0/P1/P2，仅保留 UI glue 未直接自动化的分层 P3 测试说明，不阻断本切片。 |
| `已验证` | Claude 关键 challenge | 后台任务 `c3e1ce54`、实际 `claude-opus-5`、只读 Opus/XHigh；八项关键问题经 Codex 复算，成立项全部修正，合并切片与 `DEC-047` 记录方式获认可。 |
| `已验证` | 真实 Release WPF lifecycle smoke | 主实例 PID 26200、HWND 7667926、标题 `RelayCove`、`Responding=True`；次实例 PID 42492 退出码 0、同路径仅一个进程；精确清理后残留 0。 |
| `已验证` | Drop/Clipboard 实机自动化边界 | 当前无登录 composer，且自动化按任务约束不读取或覆盖用户真实 Clipboard；因此以 STA PNG factory、Clipboard reader、FileDrop policy/snapshot 与 exact context 分层回归覆盖，未冒充真实输入事件或视觉通过。 |

### 文件范围

- 新增：Client `Attachments/` 的有界内存 stream、截图 factory/read outcome、FileDrop policy/snapshot、source-neutral draft 及对应 Client 测试。
- 修改：`App.xaml.cs`、`MainWindow.xaml(.cs)`、文件 source/outcome、upload source 注释、文件工厂测试，以及本任务、状态、执行与决策记录。
- 删除：原 `ClientAttachmentFileSelection.cs` 类型文件；其职责由重命名后的 source-neutral `ClientAttachmentDraft.cs` 承接。

### 决策与限制

- 决策：`DEC-047` 冻结 exact FileDrop、文本优先 Ctrl+V、STA 像素快照、25 MiB retained/100 MiB raw 双预算、精确私有 buffer、外部读取前后 context 门与 owner-safe 单飞取消。
- 已知限制：当前无真实登录账户，自动化不读取或覆盖用户真实 Clipboard，也不冒充 Drop/Ctrl+V 视觉、键盘或 Narrator 端到端通过；这些与下载/缓存/缩略图/查看原图/打开、VPS/双客户端一起保留到后续切片或 M5 Gate。单次工作集仍包含剪贴板 provider、冻结 BGRA32、WIC 与有界输出的短时峰值；单飞和硬上限约束并发，但不把进程级 OOM 冒充可恢复异常。

### 下一步

- 仅快进集成后进入附件下载/cache 与下载进度纵向链。
