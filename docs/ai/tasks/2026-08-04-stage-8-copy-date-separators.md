# 阶段 8 消息复制与日期分割线

## 任务定义

- **任务名称：** 阶段 8 当前消息安全复制与本地日期分组
- **状态：** `已完成`
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

- [x] 第一条及本地日期变化处显示一个日期标签；同日连续行不重复，确认到 pending 的日期边界正确。
- [x] 当前 Ready snapshot 中确认/pending 行可逐字复制展示正文；旧 snapshot、非 Ready、撤权或已切换行不写剪贴板。
- [x] Clipboard 暂时不可用时 UI 如实提示且不崩溃；正文、发送者、ID 与路径不进入日志或 `ToString()`。
- [x] Fast/Full、presenter/WPF 定向与重复、model drift、八项目漏洞审计、空白检查及真实 Windows WPF smoke 通过；不把真实剪贴板内容纳入自动化副作用。

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

- presentation 在确认消息与 pending 的既有显示顺序上按 `CreatedAt.ToLocalTime().Date` 推进边界，第一行和日期变化行显示固定 `yyyy-MM-dd` 标签；日期标签在 `ToString()` 中脱敏。
- 当前 Ready snapshot 的值相等成员门决定 Copy 资格；旧行、篡改内容、非 Ready/撤权快照与空内容 fail-closed。确认和 pending 均只复制实际展示正文，不附加身份、时间或 Reply 摘要。
- WPF 消息行增加“复制”和日期 pill。无状态 Clipboard writer 逐字传递 Unicode 内容，只把 `ExternalException` 归为可恢复占用并显示脱敏状态；其他未知异常不被静默吞掉。
- 自动化通过注入 writer 验证成功、占用和未知异常，不读取或覆盖用户真实 Clipboard；无协议、schema、排序、read-through、依赖或日志变化。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | Fast / Full | 最终 Fast 与代码提交 `ab56431` 前后等价树上的两次 Full 通过；Release 0 警告、0 错误，Shared 35 + Server 175 + Client 555 + Updater 1 = 766/766。 |
| `已验证` | presentation / Copy 定向 | 日期、当前快照门与 Clipboard writer 每轮 8 项；Release 连续 10 轮 80/80，覆盖首行、同日、跨日、confirmed→pending、精确换行/空白、旧/撤权行、Clipboard 占用与未知异常。 |
| `已验证` | EF / NuGet / 空白 | EF Core 无 pending model changes；8 个 source/test 项目无已知 vulnerable package；`git diff --check` 与 format 通过。 |
| `已验证` | 真实 Windows WPF smoke | 最终 Release 主进程 PID 46156 取得非零句柄 91295132 且响应；第二实例 PID 49028 退出码 0、同路径仅 1 个进程；精确 PID 清理后残留 0。XAML Release 编译覆盖日期、Copy 事件及状态文本。 |
| `未验证` | 真实 Clipboard 内容 / 登录视觉 | 自动化刻意不改用户 Clipboard；真实登录消息列表的日期/Copy 视觉、VPS、双客户端与 Narrator 保留 M5 Gate。 |

### 文件范围

- 新增：`ClientMessageCopyPolicy.cs`、`ClientClipboardWriter.cs` 及两组测试。
- 修改：消息项 presentation/presenter、`MainWindow.xaml(.cs)`、presenter 测试及本任务/状态账本。
- 删除：无。

### 决策与限制

- 决策：复制只使用当前 Ready 不可变快照中的实际展示正文；日期使用绝对本地日历标签，不引入午夜刷新或相对日期状态。
- 已知限制：链接、`@用户` 与新消息分割线继续独立切片；真实剪贴板不由自动化覆盖。

### 下一步

- 仅快进代码提交 `ab56431` 及本完成记录，然后继续阶段 8 链接识别或新消息分割的独立边界。
