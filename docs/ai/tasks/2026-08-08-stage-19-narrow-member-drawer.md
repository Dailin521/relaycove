# 阶段 19：窄窗口成员抽屉与可达快照

## 任务定义

- **任务名称：** 阶段 19 窄窗口成员抽屉与可达快照
- **状态：** `进行中`
- **基准提交：** `b8a7fb1fc7537b74a58673913d5d5a5292e7b5ce`
- **工作分支：** `agent/stage-19-narrow-member-drawer`
- **相关方案章节：** 阶段 8、21.1；[`../ui-design-guidelines.md`](../../ui-design-guidelines.md)
- **详细执行计划：** [`../RC24_EXECUTION_PLAN.md`](../RC24_EXECUTION_PLAN.md) 的 S2

### 目标

在窗口宽度小于 1400px 时，成员/频道抽屉不得因为用户点击而重新打开并覆盖聊天、附件、提及或发送按钮。WPF 内置快照只构造生产逻辑可达的输入状态，并验证关键操作既可见又可用。

### 已知事实

- `已验证`：`OnMainWindowSizeChanged` 只会在已打开抽屉后缩窄窗口时关闭它；`OnOpenChannelPanelClicked` 仍可在窄窗口无条件打开抽屉。
- `已验证`：1280×720 快照目前同时构造正文、提及候选和十个附件，而生产逻辑在选附件时禁用正文和 `@`。
- `已验证`：S0/S1 Fast、定向、快照和 Full 已通过；独立快照审阅将该问题定为 P2。

### 范围

- 必须实现：
  - 在窄窗口点击成员/频道入口时保持抽屉关闭，保留聊天输入区可用，并提供简短可访问提示。
  - 添加该真实点击路径的 WPF 回归测试。
  - 将 1280 快照拆为可达的“回复+附件”和“回复+提及”状态，断言附件、提及和发送操作可见且可用。
  - 重新生成三档 WPF 快照，并用内部图片查看工具审阅。
- 允许修改：
  - `src/RelayCove.Client/MainWindow.xaml.cs`
  - `tests/RelayCove.Client.Tests/Desktop/ClientUiSnapshotTests.cs`
  - 本任务、`STATUS.md` 和 rc.24 计划的真实状态记录
- 明确不做：
  - 不改 Shared/Server API、SignalR、数据库、成员授权或消息可靠性语义。
  - 不在窄窗口引入新的模态成员 UI、UI 框架或桌面自动化。

### 验收标准

- [ ] 1280px 下点击成员入口后抽屉保持关闭，聊天区无右边距，发送相关操作可用。
- [ ] 1600px 下成员抽屉仍可打开，现有成员管理行为不回归。
- [ ] 附件与提及快照状态均为生产可达状态，并通过布局/可用性断言。
- [ ] WPF 快照定向、Fast 和 Full 通过，Release 0 警告、0 错误。

### 验证命令

```powershell
dotnet test ./tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ClientUiSnapshotTests"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
```

### 停止并询问

- 修复若需要改变频道管理权限、公共协议或宽度之外的布局体系时停止。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 任务结果

### 修改摘要

- 待执行。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | 窄窗口成员点击回归 | 待执行 |
| `未验证` | 可达附件/提及快照 | 待执行 |
| `未验证` | Fast 与 Full | 待执行 |

### 文件范围

- 新增：`docs/ai/tasks/2026-08-08-stage-19-narrow-member-drawer.md`
- 修改：无
- 删除：无

### 决策与限制

- 决策：窄窗口优先保证聊天与发送，成员管理要求用户扩大窗口。
- 已知限制：真实 Windows 双账号和系统通知矩阵仍需后续环境验证。

### 下一步

- 实现窄窗口成员入口 guard，并补充可达 WPF 快照回归。
