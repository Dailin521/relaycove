# Stage 24.11 — 跳转到最新消息

Date: 2026-08-21
Status: local candidate; Visual Studio confirmation pending

## Problem

消息列表原有浮动按钮只在离开底部后又收到新消息时出现。用户要求：即使没有新消息，只要向上滚动超过两个当前消息视口高度，也显示参考图中的“跳转到最新消息”按钮，点击后直接回到最新消息。

这是本轮唯一问题。不改变分页、消息协议、已读权威状态、实时跟随、Composer 或其他界面。

## Implementation

- Windows 原生列表同时读取 `ScrollableHeight - VerticalOffset` 与 `ViewportHeight`，以 `bottomDistance > 2 × viewportHeight` 作为严格阈值；恰好两屏不显示。
- 阈值状态与新消息计数共同决定同一个浮动入口是否可见。按钮固定显示“跳转到最新消息”，使用绿色双下箭头、raised surface、细边框和胶囊圆角，位于消息区底部居中。
- 点击复用既有 `ManualJumpToLatest` 请求；原生列表仍按目标实现、视口交集和真实底距 `<=2 DIP` 确认。确认后清除阈值状态与新消息计数。
- 会话导航/切换会清除阈值状态。程序化滚动请求活跃期间不采纳中间视口位置，因此不会在激活或跳转途中误显示按钮。
- 视口报告携带该原生列表当前所属的会话键与 history generation；切换会话后的旧 `Scrolled`/布局回调若与当前会话不匹配会被丢弃，不能把旧按钮状态带到新会话。
- 既有 `<=96 DIP` 自动跟随语义、older-page prepend 锚点和 realtime follow-scroll 保持不变。

## Deterministic evidence

```powershell
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-11-review-tests/ --filter "FullyQualifiedName~ComposerResizeAndViewportPolicyTests|FullyQualifiedName~MessageViewport_WhenMoreThanTwoPagesFromLatest|FullyQualifiedName~MessageViewport_WhenOldConversationReportsAfterSwitch|FullyQualifiedName~Messages_WhenViewportIsAwayFromBottom"
# 17/17 passed; App/XAML/resources built successfully

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-11-final-app-tests/
# 202/202 passed
```

未运行 Fast、Full、Live、打包、生产 Realm 或真实写入。未启动、移动、操作或截图用户的 MAUI 窗口。

独立只读复核发现并关闭了一个跨会话迟到视口回调的 P1；复核确认当前没有剩余 P0/P1。

## Visual Studio short check

1. 打开一个至少有数屏历史的频道或私信，保持当前在最新消息；按钮应隐藏。
2. 向上滚动：真实底距不超过两个当前视口高度时按钮仍隐藏；严格超过两屏后，底部居中出现“跳转到最新消息”。
3. 点击按钮，确认列表直接回到最新消息并隐藏按钮，没有触发额外向上分页抖动或中段停留。
4. 在离底超过两屏时切换到另一个会话，确认新会话不会继承旧会话的按钮状态。

Manual result: pending user Visual Studio confirmation.
