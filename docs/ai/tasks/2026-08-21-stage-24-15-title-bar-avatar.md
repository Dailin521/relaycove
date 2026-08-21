# Stage 24.15 — 标题栏账号头像

Date: 2026-08-21
Status: local candidate; Visual Studio confirmation pending

## Problem

Stage 24.14 移除最左侧主导航栏后，原本位于该栏顶部的当前用户头像也不再显示。用户确认两列布局后，要求把本人头像放在右上标题栏，并同意头像位于设置图标左侧、点击后打开账号菜单的方案。

这是本轮唯一问题。Stage 24.11 至 Stage 24.14 的未提交候选保留在当前 `main` 工作树；没有修改 RelayCove.Web、协议、缓存或服务器状态。

## Implementation

- 标题栏产品动作顺序改为“当前用户头像、设置、主题、置顶”。头像沿用 `CurrentUserAvatarUrl` 和现有 `RealmMediaImageView` 受控加载，不把 API Key 放入 URL；没有可显示头像时回退 `CurrentUserInitial`。
- 头像点击执行现有 `ToggleAccountMenuCommand`。账号菜单改为从主内容右上方出现，继续显示本人头像、姓名、工作区、账户设置和注销。
- 内部截图发现最初版本在标题栏只显示空白蓝圆：`TitleBar.TrailingContent` 没有把 ViewModel 继承给头像子树。最终由 `ProductBarView.Bind` 对头像可见性、首字、受控头像 URL 和点击命令使用显式 source 绑定；账号菜单和设置选中态通过 `ProductBarView` bindable proxy 驱动。
- 头像菜单打开时显示描边选中状态；设置齿轮继续独立切换应用设置页，不把两个入口混为同一动作。曾尝试在 `Bind` 中修改已经应用的 XAML `DataTrigger.Binding`，该方案以 `0x80131509` 在 HWND 创建前退出，已被删除，没有进入最终实现。

## Deterministic evidence

首次使用默认输出运行 App 测试时，用户当时运行的 `RelayCove.App` PID 13356 锁定输出 EXE，触发 `MSB3026/MSB3027/MSB3021`；没有停止或操作该进程与窗口。该次失败与头像代码无关。早期候选随后曾在隔离输出通过 213/213，但这发生在最终标题栏绑定修正之前，不能作为最终代码证据。

用户随后明确要求以 `main` 主项目为准、不使用测试项目。最终修正只执行 App-only Debug 离线预览构建：

```powershell
pwsh ./scripts/start-maui-preview.ps1 -Scene account-menu -Theme light -Width 1440 -Height 900
# passed; 0 warnings, 0 errors
```

最终无输入内部检查在 `DISPLAY2`、1440×900 DIP、150% 缩放完成：

- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-15-account-menu-fixed.png`，SHA-256 `9EB03B5E616EB3CB4D571BEA5EFCE39610907CBA3B8F028C642B96F8A0CA3D4D`；账号菜单右上定位且标题栏显示 `林`。
- `C:\Users\Administrator\AppData\Local\Temp\relaycove-stage24-15-shell-fixed.png`，SHA-256 `459E27C26E52696A5826CD4AB566673B84311CFAC8BF687D09CAFF52F930A0B7`；普通 shell 标题栏显示 `林`，动作间距正常。

截图仅作 Agent 内部检查，没有在对话中交付。捕获未移动鼠标、未发送点击或键盘输入；预览使用 `NativeShellPreviewSession`，不连接 Zulip。检查后只停止了启动器记录且路径属于本工作树的预览 PID。最终修正未运行任何测试项目，也未运行 Fast、Full、Live、打包、生产 Realm 或真实写入。

## Visual Studio short check

1. 登录后确认右上产品动作顺序为本人头像、设置、主题、置顶；头像为圆形且没有挤压系统窗口按钮。
2. 有头像时应显示真实头像；头像缺失或加载失败时应显示本人姓名首字母，不出现错误文本。
3. 点击头像，账号菜单应在右上方打开并显示姓名、工作区、账户设置和注销；再次点击头像或点击外部应关闭。
4. 点击齿轮仍应打开应用设置；头像菜单与设置页不会同时残留。

Manual result: pending user Visual Studio confirmation.
