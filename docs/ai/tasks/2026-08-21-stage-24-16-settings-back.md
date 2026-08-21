# Stage 24.16 — 设置页返回聊天

Date: 2026-08-21
Status: local candidate; Visual Studio confirmation pending

## Problem

移除左侧主导航栏并把设置入口移动到标题栏后，设置页内部没有明确返回入口。虽然再次点击标题栏齿轮可以切回消息区，但页面本身缺少可发现的返回路径。用户要求补上返回。

这是本轮唯一问题。Stage 24.11 至 Stage 24.15 的未提交候选保留在当前 `main` 工作树；没有修改 RelayCove.Web、设置数据、协议、缓存或服务器状态。

## Implementation

- 宽屏设置页在左侧分类目录顶部加入带返回箭头的“返回聊天”，位于“设置”标题之前。
- 窄屏设置页在顶部横向分类栏首项加入同一“返回聊天”，后续外观、通用、通知、存储、账户仍可水平滚动访问。
- 两个入口都直接执行现有 `ShowMessagesCommand`，只切回消息区并保留当前会话，不引入第二套返回状态。

## Deterministic evidence

按用户“以 `main` 主项目为准、不使用测试项目”的要求，只运行 App-only Debug 离线预览：

```powershell
pwsh ./scripts/start-maui-preview.ps1 -Scene settings -Theme light -Width 1440 -Height 900
# passed; 0 warnings, 0 errors
```

内部无输入截图均来自 `DISPLAY2`、150% 缩放，预览使用 `NativeShellPreviewSession`，不连接 Zulip：

- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-16-settings-back-wide.png`，1440×900 DIP，SHA-256 `0789CA1060715B12617E6FAAA2FCCDA0A75BA6C0E0920BC69286ECCF2BBEC72B`；返回入口位于左侧目录顶部。
- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-16-settings-back-narrow.png`，640×900 DIP，SHA-256 `54DF40EA4E363569DEF03908263C63A2CD06567F5AC4A7B37171AF5B72F57DE4`；返回入口是顶部分类栏第一项且未裁切设置内容。

截图仅作 Agent 内部检查，没有在对话中交付，也没有移动鼠标或发送输入。检查后只停止了启动器记录且路径属于本工作树的预览 PID。未运行测试项目、Fast、Full、Live、打包、生产 Realm 或真实写入。

## Visual Studio short check

1. 打开设置：宽屏左侧顶部应显示“返回聊天”。
2. 点击返回：应回到此前消息区，当前频道/私聊保持不变。
3. 将窗口收窄：顶部横向分类栏第一项仍应为“返回聊天”，外观、通用、通知、存储、账户仍可访问。

Manual result: pending user Visual Studio confirmation.
