# Stage 34 — reaction 表情面板定位

Status: code complete; awaiting user Visual Studio confirmation

## Scope

- 从消息快捷“笑脸”按钮打开 reaction 表情面板时，面板必须跟随该按钮和当前消息位置，不得固定出现在窗口左上角。
- 面板横向限制在聊天内容列内；纵向根据按钮下方空间自动放在按钮下方或上方，并继续受窗口边缘约束。
- 不改变表情分类、横向拖动、emoji 集合、reaction 写入、消息菜单或 Composer 表情面板。

## Diagnosis

- reaction 面板已使用 `PopoverAnchorBehavior`，但 XAML 错误绑定了 `MessageMenuAnchorX/Y`。
- 从消息更多菜单进入时这组坐标存在；从消息行快捷笑脸直接进入时只调用 `OpenReactionPickerCommand`，没有记录任何坐标，因此绑定值保持默认 `(0,0)`，面板稳定落在主内容左上。
- 被否决的方案包括增加固定 Margin、按窗口尺寸硬编码位置或复用当前滚动偏移。这些方案不能跟随虚拟化消息行，也会在缩放、滚动和窄窗口下漂移。

## Final implementation

- 新增独立 `ReactionPickerRequest` 与 `ReactionPickerAnchorX/Y`，reaction 面板不再读取消息菜单坐标。
- 快捷笑脸点击时从 WinUI 原生控件转换到页面坐标，并把 310 DIP 面板的首选左边界限制在当前 `MessageListView` 内容列内；本人消息按按钮右侧对齐，他人消息按按钮左侧对齐。
- `PopoverAnchorBehavior` 继续负责上下翻转和最终窗口边缘保护。无法取得原生坐标时保留现有安全回退，不阻断 reaction 功能。

## Deterministic validation

- 首次定向测试暴露新增 ViewModel 测试夹具缺少 recent-DM 投影，导致没有会话行；补齐与真实 register 一致的最近私聊数据后重新运行。
- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ReactionPicker_WhenRendered_UsesItsOwnTriggerAnchor|FullyQualifiedName~OpenReactionPickerAtCommand_WhenInvoked_StoresTheQuickActionAnchor|FullyQualifiedName~PopoverAnchorBehaviorTests" -p:UseAppHost=false -p:OutputPath=.verify/stage34-reaction-anchor-tests-2/` — passed 4/4.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage34-reaction-anchor-build/` — passed with 0 warnings and 0 errors.

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Shortest manual check

1. 在消息列表中滚动到窗口中部或底部，点击一条消息的快捷笑脸，确认面板出现在该消息附近且不进入左侧会话栏。
2. 分别检查靠近聊天区顶部和底部的消息，确认空间不足时面板能上下翻转。
3. 缩窄窗口后再打开一次，确认面板仍在可见窗口内，分类横向拖动和选 emoji 正常。
