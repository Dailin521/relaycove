# 阶段 9 WPF 附件拖拽与粘贴截图

## 任务定义

- **任务名称：** 阶段 9 WPF 文件拖拽与有界内存截图粘贴入口
- **状态：** `进行中`
- **基准提交：** `8ac80fb9b70953f6ff2e56d38b6278f2f765e2b7`
- **工作分支：** `agent/stage-9-wpf-attachment-inputs`
- **相关方案章节：** 2.1、14.1–14.2、阶段 9、21.3；`DEC-045/046`

### 目标

让当前 Ready 会话在正文为空且没有 mention 时，可以把 Windows `FileDrop` 文件拖入现有附件草稿，也可以在 composer 输入区用 Ctrl+V 把剪贴板位图异步编码为 PNG 草稿；两种入口复用现有 1–10 项、exact context/draft、content-copy progress 和 durable pending/no-replay 链。

### 已知事实

- `已验证`：绿色集成头、本分支基线与远端 `agent/v1-integration` 都是 `8ac80fb9b70953f6ff2e56d38b6278f2f765e2b7`；新分支 Fast 1045/1045（Shared 39、Server 255、Client 750、Updater 1），Debug 构建 0 警告、0 错误。
- `已验证`：现有文件 factory 已提供原子 1–10 项、路径去重、名称/大小/可读性、可重开 stream 与路径脱敏；composer 已提供 exact conversation/context/draft 门、PendingCommitted 清理和上传进度。
- `已验证`：`ClientAttachmentUploadSource` 只要求每次返回可读、seekable、精确剩余长度的 stream，不要求磁盘路径；冻结的 PNG 字节可以通过每次新建只读 `MemoryStream` 满足 401 reopen，无需临时文件或 schema。
- `已验证`：Client 是 Windows WPF TFM 且无新依赖即可使用 `FileDrop`、`Clipboard.GetImage`、`BitmapSource` 和 `PngBitmapEncoder`；当前仓库没有拖拽或位图剪贴板读取实现。
- `已验证`：Claude Code 2.1.221 关键只读 challenge 已以后台任务 `c3e1ce54` 启动，只允许 Read/Glob/Grep；Codex 不等待其串行推进，结论只作独立反证。

### 假设

- `假设`：拖拽只接受真实 `DataFormats.FileDrop`，不接受 shell 虚拟文件、URL、文本、目录或自动转换；Drop 前复制路径快照，之后完全复用文件 factory，全批成功或全批失败。
- `假设`：截图入口只在 composer 输入区收到 Ctrl+V 且剪贴板明确含位图时截获；无位图时保留 TextBox 默认文本粘贴。读取必须在 UI STA 完成，转为冻结 BGRA32 后在后台编码 PNG。
- `假设`：编码输出写入硬上限 stream，单个截图 PNG 不超过既有 100 MiB；所有内存截图草稿的 retained buffer 合计也不超过 100 MiB，原始 BGRA32 像素字节不超过同一上限，避免 10 个草稿放大到无界进程内存。该限制只约束截图内存草稿，不降低普通文件的现有协议上限。
- `假设`：同一截图重复粘贴视为用户显式创建多个独立草稿，不按内容 hash 去重；截图内容、尺寸、hash、编码字节和 synthetic identity 都不进入日志、错误、SQLite、网络额外字段或 `ToString()`。

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

- [ ] FileDrop 只在当前可接受 composer 状态显示 Copy；有效文件与按钮选择行为一致，目录/虚拟/无效/超限/重复批次原子拒绝且不显示路径。
- [ ] Ctrl+V 图片在 UI STA 取得快照后后台编码为规范 PNG，source 可至少重开两次且内容一致；无图不截获文本粘贴，占用/无效/取消/像素或编码上限均 fail-closed。
- [ ] 普通文件与截图合计最多 10 项；全部图片仍为 Image，混合文件为 File；重复截图是独立显式草稿，路径文件仍跨批去重。
- [ ] 拖拽/截图 await 期间的会话切换、A→B→A、草稿变化或账户结束不会添加旧结果；pending 前失败保留 exact 草稿，pending 后沿用 `DEC-045/046` 原键 retry 且不重编码/重传。
- [ ] 定向、Fast、最终一次 Full、真实 Release WPF lifecycle/可行的 Drop+clipboard smoke、Codex reviewer、关键 Claude challenge 记录、日志脱敏和空白检查完成。

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

- 待完成。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | 新分支 Fast 基线 | 1045/1045；Shared 39、Server 255、Client 750、Updater 1；0 警告、0 错误。 |
| `进行中` | Claude 关键 challenge | 后台任务 `c3e1ce54`，只读 Opus/XHigh；等待终态。 |
| `未验证` | 本任务最终门禁 | 实现完成后填写。 |

### 文件范围

- 新增：待完成。
- 修改：待完成。
- 删除：待完成。

### 决策与限制

- 决策：待 Claude challenge、实现与 Codex reviewer 收敛后记录为 `DEC-047`。
- 已知限制：下载/缓存/缩略图/查看原图/打开和 VPS 留在后续切片。

### 下一步

- 完成本切片后进入附件下载/cache 与下载进度纵向链。
