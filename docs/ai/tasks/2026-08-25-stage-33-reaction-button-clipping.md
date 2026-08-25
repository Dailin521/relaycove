# Stage 33 — Windows reaction 按钮右侧裁切

Status: completed and accepted by user in Visual Studio

## Scope

- 修复 MAUI Windows 消息气泡中 reaction 按钮右侧圆角、背景和边框被裁掉的问题。
- 保留按钮按内容自适应、多 reaction 换行、当前用户选中态、点击切换、键盘按钮语义和消息气泡短内容自适应。
- 不改 reaction 协议身份、计数、emoji 字形目录、服务器写入或消息正文。

## Diagnosis

- 用户截图中的两个 reaction 都只缺右侧按钮轮廓；emoji 与计数本身完整，气泡右侧仍有足够空间，因此不是父气泡宽度不足或字符字体裁切。
- reaction `Button` 直接作为可换行 `FlexLayout` 子项，并由 Button 自身 `Margin="0,0,4,4"` 提供项目间距。Windows MAUI 在该组合的原生测量槽中没有为右侧圆角绘制保留完整空间，背景和 border 在每个子项自己的右边界被裁切。
- 被否决的方案包括给 reaction 或气泡增加固定宽度、扩大所有消息气泡、缩小 emoji/字体，以及只修改圆角数值。这些方案会破坏内容自适应或仅掩盖测量问题。

## Final implementation

- 每个 reaction 使用一个带右/下 Padding 的轻量 Grid 作为 Flex 子项；原生 Button 自身改为零 Margin。
- 间距现在属于外层 Grid 的测量尺寸，Button 的完整自适应宽度和右侧圆角都位于自己的布局槽内。
- `ReactionButtonStyle` 不再注入 Margin；高度、Padding、字体、边框、选中态和点击处理保持不变。

## Deterministic validation

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MessageReactions_WhenRendered_KeepSpacingOutsideNativeButtons|FullyQualifiedName~MessageBubble_WhenContentIsShort" -p:UseAppHost=false -p:OutputPath=.verify/stage33-reaction-tests/` — passed 2/2.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage33-reaction-build/` — passed with 0 warnings and 0 errors.

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Manual result

- 用户以同一条包含多个 reaction 的短消息复验后确认“很好”，右侧圆角、背景和边框裁切已消失。

## Shortest manual check

1. 在 Visual Studio 打开包含两个以上 reaction 的短消息，确认每个按钮左右圆角、背景与边框都完整。
2. 点击未选和已选 reaction，确认选中态切换后仍不裁切，计数与服务器行为不变。
3. 缩窄窗口，确认 reaction 能自然换行且短正文气泡仍按内容收缩。
