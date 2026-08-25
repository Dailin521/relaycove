# Stage 32 — MAUI 收藏消息入口

Status: code complete; awaiting user Visual Studio confirmation

## Scope

- 在不恢复旧主导航栏、联系人入口或第三列的前提下，让用户能从 MAUI 主界面进入当前账号的收藏消息列表。
- 入口放在左上当前用户头像打开的账号菜单中；收藏列表复用现有右侧内容区，宽窗口继续保留左侧统一会话列表。
- 继续使用现有 Zulip `starred` 权威读取、分页、跳转消息和取消收藏能力，不增加本机收藏副本，也不改变消息菜单中的收藏写入。

## Diagnosis

- Core、session 和 `ShellViewModel` 已具备 `LoadSavedMessagesAsync`、`ShowSavedCommand`、分页、按消息跳转及取消收藏的完整能力。
- Stage 25 移除旧头像/消息/联系人/已保存主导航栏时，`MainPage.xaml` 同时移除了收藏列表的渲染和入口；因此消息仍可收藏，但 MAUI 内没有路径重新查看。
- 缺陷属于主壳层信息架构断链，不需要新增协议、缓存或服务器写入。

## Final implementation

- 账号菜单首项新增“收藏的消息”，调用现有 `ShowSavedCommand`；账户设置与注销继续保留。
- 主会话工作区同时承载消息区和收藏区。宽/中窗口的收藏列表显示在原聊天内容列，左侧统一会话列保持不变；窄窗口直接显示收藏内容。
- 收藏页提供返回聊天、刷新、服务器读取状态、空状态、分页、跳转到原消息和取消收藏。跳转成功后回到消息区并定位目标消息。
- 未恢复 `NavigationRailView`、联系人页、第三列、公开频道或 Web 入口。

## Manual feedback and anchor correction

- 用户首次 Visual Studio 检查确认入口正常，但“跳转到消息”只打开了对应会话的当前加载页底部，没有定位到被收藏的真实消息。
- 服务端 `OpenMessageAsync` 已正确按收藏 message ID 读取前后文；缺陷发生在 App：around page 发布后被误用普通“会话激活”滚动原因，ViewModel 取该页最大 message ID，原生列表再执行 latest/bottom 定位。
- 被否决的方案是继续在服务器读取或收藏列表上补偿跳转，也不允许先到底部再猜测偏移；这些做法既重复现有权威读取，也无法稳定定位虚拟化列表中的目标行。
- 最终增加独立 `MessageAnchor` 滚动原因。收藏打开完成后以真实收藏 message ID 覆盖普通激活滚动；原生列表只把该目标滚到可见区域中央，不强制到底部，也不把锚点定位误判为“已到最新消息”或触发最新位置的已读门禁。

## Deterministic validation

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MainShellLayoutTests|FullyQualifiedName~ShowSaved_WhenOpenedFromAccountMenu|FullyQualifiedName~LoadOlderSaved_WhenServerAnchorIsMissing|FullyQualifiedName~ServerSearchAndSaved_WhenResultsContainHiddenConversations" -p:UseAppHost=false -p:OutputPath=.verify/stage32-tests/` — passed 12/12.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage32-build/` — passed with 0 warnings and 0 errors.
- 首次人工反馈后的锚点回归：`dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --filter "FullyQualifiedName~OpenSavedMessage_WhenAroundPageLoads_QueuesExactMessageAnchor|FullyQualifiedName~ShowSaved_WhenOpenedFromAccountMenu|FullyQualifiedName~LoadOlderSaved_WhenServerAnchorIsMissing" -p:UseAppHost=false -p:OutputPath=.verify/stage32-anchor-tests/` — passed 3/3.
- 锚点修正后的 `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage32-anchor-build/` — passed with 0 warnings and 0 errors.

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Shortest manual check

1. 在 Visual Studio 启动 MAUI，点击左上本人头像，确认菜单首项为“收藏的消息”，且账户设置和注销仍存在。
2. 点击“收藏的消息”，确认宽窗口左侧会话列保留，右侧显示收藏列表；窄窗口不出现空白列。
3. 对一条不在当前会话底部的旧收藏点击“跳转到消息”，确认目标收藏本身进入可见区域，而不是落到 around page 或会话底部；再检查“取消收藏”。
4. 点击“返回聊天”，确认回到之前的聊天区，没有恢复旧导航栏或第三列。
