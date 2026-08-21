# Stage 24.14 — MAUI 两列主壳层

Date: 2026-08-21
Status: user-confirmed local candidate; commit/push pending

## Problem

MAUI 主窗口当前由最左侧主导航栏、会话列表和聊天区组成三列。用户不需要“消息、联系人、已保存”的主导航入口，要求移除整条左侧栏，只保留会话列表与聊天两列，并把左下角设置移动到右上角。

这是本轮唯一问题。Stage 24.11、Stage 24.12 和 Stage 24.13 的未提交候选保留在当前 `main` 工作树；没有修改 RelayCove.Web、Zulip 协议、缓存行为或服务器状态。

## Implementation

- `MainPage` 不再实例化 `NavigationRailView`，也不再渲染联系人和已保存页面。正常聊天主界面由会话列表与聊天区组成；聊天设置仍是用户显式打开的可折叠面板，不是常驻第三主列。
- 产品标题栏右侧新增设置图标。点击进入现有设置页；设置已打开时保持选中视觉，再次点击返回消息区，避免移除左栏后失去返回路径。
- 联系人与已保存的底层 ViewModel/协议能力未在这个 UI-only 问题中删除，避免扩大范围；它们不再有 MAUI 主壳层入口。
- 移除主导航栏后，消息行最大宽度不再扣除旧栏的 48/60 DIP。会话列表宽度偏好和窄/中/宽断点保持不变。

## Deterministic evidence

```powershell
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore
# 213/213 passed

dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore
# passed; 0 warnings, 0 errors
```

新增回归覆盖两列主 XAML、不再挂载主导航/联系人/已保存页面、右上设置命令绑定、设置与消息区往返，以及移除旧栏后的响应式消息宽度。Stage 24.13 已记录的仓库级 CRLF/import-order Fast 门禁没有因本轮 UI 修改变化，因此未重复运行 Fast。未运行 Full、Live、打包、生产 Realm 或真实写入。未启动、移动、操作或截图用户的 MAUI 窗口。

## Visual Studio short check

1. 登录后确认最左侧头像/消息/联系人/已保存/设置栏完全消失，默认只剩会话列表与聊天区。
2. 检查会话列表从窗口内容最左侧开始，聊天区正常占用剩余空间，1024×768 下无横向滚动或异常空白。
3. 点击右上角设置图标，应打开原设置页并显示选中状态；再次点击同一图标，应返回原消息区。
4. 打开频道或一对一私聊右上省略号，确认会话设置面板仍能正常显示和关闭。

Manual result: user confirmed the two-column result as good on 2026-08-21, then requested the separate Stage 24.15 title-bar avatar follow-up.
