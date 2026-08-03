# 阶段 8 消息复制与日期分割线

## 任务定义

- **任务名称：** 阶段 8 当前消息安全复制与本地日期分组
- **状态：** `进行中`
- **基准提交：** `b1bf9c33a9c58afba89ecc0d874228cd5b89bb29`
- **工作分支：** `agent/stage-8-copy-date-separators`
- **相关方案章节：** 9.2–9.4、18.4、阶段 8；`DEC-034`、`DEC-035`、`DEC-037`

### 目标

在当前 Ready、账户隔离的消息窗口中为每条可见消息提供显式复制正文操作，并在第一条消息及本地日历日期变化处显示稳定日期分割线。复制只能使用当前不可变快照中的行，不把正文写入日志或对象格式化；日期只改变 presentation，不改变服务端时间、缓存顺序、分页或 read-through。

### 已知事实

- `已验证`：绿色集成头 `b1bf9c3` 已包含 Reply 切片全部门禁；当前分支建立时工作树干净。
- `已验证`：消息窗口使用不可变 `ClientMessageListItemPresentation` 列表，确认消息按 ServerMessageId 升序、pending 按 CreatedAt/LocalId 追加；当前 presentation 已保存渲染正文与本地化时间字符串，但尚无日期分割字段或 Copy 操作。
- `已验证`：非 Ready、撤权、账户/会话切换时 coordinator 发布空消息列表；WPF 丢弃旧 revision，并保存当前已应用 `displayedMessageSnapshot`，可作为复制前的 fail-closed 当前行门。
- `已验证`：WPF Dispatcher 为 STA，系统 Clipboard 可在显式用户点击中同步调用；剪贴板临时占用应显示可恢复失败，不记录或回传正文。

### 假设

- `假设`：第一版“复制消息”只复制当前行实际展示的正文/占位文本，不附加发送者、时间或引用摘要；确认与 pending 行都可复制。
- `假设`：日期分割按每条消息 `CreatedAt.ToLocalTime().Date` 计算，标签固定 `yyyy-MM-dd`，避免相对“今天/昨天”在午夜后静默过期；第一条永远显示，随后只在日期变化时显示。

### 范围

- 必须实现：
  - presentation 暴露 `ShowDateSeparator`、`DateSeparatorLabel` 与 `CanCopy`；确认与 pending 的合并显示顺序上正确推进本地日期边界。
  - WPF 在日期边界上方显示分割线；每条可复制行提供“复制”按钮。
  - Copy handler 必须确认事件行仍属于当前 Ready snapshot，再把展示正文逐字写入 Unicode Clipboard；剪贴板占用只显示脱敏失败状态。
  - 覆盖同日、跨日、确认到 pending 边界、首行、当前/陈旧/非 Ready 行门及 `ToString()` 脱敏。
- 允许修改：
  - Client Accounts presentation、`MainWindow.xaml(.cs)` 与对应 Client 测试；必要的 `docs/ai/` 记录。
- 明确不做：
  - `@用户`、链接识别/外部浏览器、未读分割线、附件复制、富文本、复制发送者/时间/引用、剪贴板历史管理、新依赖或协议/schema 变化。

### 验收标准

- [ ] 第一条及本地日期变化处显示一个日期标签；同日连续行不重复，确认到 pending 的日期边界正确。
- [ ] 当前 Ready snapshot 中确认/pending 行可逐字复制展示正文；旧 snapshot、非 Ready、撤权或已切换行不写剪贴板。
- [ ] Clipboard 暂时不可用时 UI 如实提示且不崩溃；正文、发送者、ID 与路径不进入日志或 `ToString()`。
- [ ] Fast/Full、presenter/WPF 定向与重复、model drift、八项目漏洞审计、空白检查及真实 Windows WPF smoke 通过；不把真实剪贴板内容纳入自动化副作用。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ClientMessageListPresenter"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变消息排序、缓存 schema、Shared/Server 协议、read-through/未读边界，或引入富文本/Clipboard 新依赖。
- 需要后台读取/修改用户剪贴板、自动打开外部 URI，或读取 VPS 配置才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4/18.4/阶段 8、docs/ai/STATUS.md 和本任务。
只实现当前 Ready 消息复制与本地日期分割；不实现 @、链接、未读分割、附件或协议变化。
复制前核对当前不可变快照成员，Clipboard 失败只显示脱敏状态；日期分割不改变排序或 read-through。
完成后运行全部门禁并更新证据。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | 实现与最终门禁 | 任务进行中。 |

### 文件范围

- 新增：待完成。
- 修改：待完成。
- 删除：无。

### 决策与限制

- 决策：待完成。
- 已知限制：链接、`@用户` 与新消息分割线继续独立切片；真实剪贴板不由自动化覆盖。

### 下一步

- 完成复制/日期分割闭环、门禁和绿色集成。
