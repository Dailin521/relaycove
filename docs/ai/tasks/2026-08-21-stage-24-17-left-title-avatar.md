# Stage 24.17 — 左上角账号头像

Date: 2026-08-21
Status: local candidate; user-confirmed in Visual Studio; not committed or pushed

## Problem

两列主壳层完成后，产品标题栏左侧仍有独立的蓝色 `R` 图标，而当前用户头像位于右侧动作区。用户要求去掉 `R`，把头像移到左上角。

这是本轮唯一问题。Stage 24.11 至 Stage 24.16 的未提交候选保留在当前 `main` 工作树；没有修改账号数据、协议、缓存或服务器状态。

## Implementation

- 删除 `TitleBar.LeadingContent` 中的独立 `R` 标识，将 Stage 24.15 已有的账号头像按钮整体移到该位置。
- 根据首轮视觉反馈，将左侧 leading content 的起始留白从 3 DIP 增加到 8 DIP，让头像、产品标题和工作区名称整体右移 5 DIP。
- 保留头像的受控 Realm 媒体加载、姓名首字母回退、显式 ViewModel 绑定、选中描边和账号菜单命令。
- 右侧标题栏动作只保留设置、主题和置顶；账号菜单由主内容右上改为左上对齐，继续显示当前身份、账户设置和注销。
- 同步更新静态布局回归断言，但按用户要求不运行测试项目。

## Deterministic evidence

按用户“以 `main` 主项目为准、不使用测试项目”的要求，只运行 App-only Debug 离线预览：

```powershell
pwsh ./scripts/start-maui-preview.ps1 -Scene shell -Theme light -Width 1440 -Height 900
# passed; 0 warnings, 0 errors
pwsh ./scripts/start-maui-preview.ps1 -Scene account-menu -Theme light -Width 1440 -Height 900 -NoBuild
```

内部无输入截图均来自 `DISPLAY2`、150% 缩放，预览使用 `NativeShellPreviewSession`，不连接 Zulip：

- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-17-avatar-left-shell.png`，1440×900 DIP，SHA-256 `2FBAA3CAA126166463940E45BF9964E67F603F11B99ECB0B482FAE861459D7FA`；左上显示 `林` 回退，独立 `R` 消失，右侧仅有设置、主题和置顶。
- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-17-avatar-left-menu.png`，1440×900 DIP，SHA-256 `1DDC8BAA3655EED53DE58A3B19F7CF96922D4264C505C9D575FC8C6D5558B7C1`；账号菜单从主内容左上展开。
- 首轮反馈后重新构建并生成 `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-17-avatar-left-spacing.png`，1440×900 DIP，SHA-256 `DA6B4E8740A3D8838240E435A7EFE0E03F13D7A5538D6592D4A0F63298B71518`；确认最终 8 DIP 左侧留白下头像、标题与工作区名称整体右移且没有裁切。

截图仅作 Agent 内部检查，没有在对话中交付，也没有移动鼠标或发送输入。检查后只停止了 PID `42236`，其可执行路径位于本工作树 `artifacts\maui\preview-builds`。未运行测试项目、Fast、Full、Live、打包、生产 Realm 或真实写入。

## Visual Studio short check

1. 主窗口左上应显示本人头像或姓名首字母，不再显示蓝色 `R` 图标。
2. 右上应只保留设置、主题、置顶和系统窗口按钮。
3. 点击左上头像：账号菜单应从主内容左上展开，账户设置与注销仍可见。

Manual result: passed. The user confirmed the final left spacing and then approved Stage 24.17 as the basis for Stage 25. The left account avatar remains clear of the window edge, the separate `R` mark is absent, the right-side actions remain Settings/Theme/Pin, and the account menu behavior is retained. This confirmation does not change the recorded test, Realm-write, commit or push boundaries above.
