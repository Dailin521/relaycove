# Stage 37 — 当前会话收到消息时稳定追加

Status: completed and accepted by user in Visual Studio

## Scope

- 仅修复当前前台聊天已位于底部时，对方实时发来消息造成的“加载最新消息”短暂出现、布局闪动和随后刷新。
- 自己发送消息、离开底部后的新消息提示、后台会话未读、服务器标读和滚动状态机保持原行为。

## Diagnosis

- 自己发送的消息进入投影时已经是已读，因此直接追加。
- 对方的实时消息先以权威 `IsRead=false` 进入当前会话，消息列表会短暂插入未读分隔/最新消息提示；当前前台底部会话随后按既有门禁提交标读，服务器 flags 回来后又删除该临时元素。前后两次行高变化造成了用户看到的闪动和刷新。
- 被否决的方案包括提前把消息改成本机已读、直接清除 Core 未读，以及对所有当前会话都隐藏未读提示。这些做法会越过服务器权威，或破坏用户已经向上阅读时的新消息提醒。

## Final implementation

- 只在以下条件全部成立时，隐藏“本轮新增消息”的临时未读分隔：会话未切换、主窗口前台激活、消息区和聊天内容可见、没有遮罩或导航、当前视口近底、连接正常且当前会话历史已稳定。
- 抑制边界使用投影前的最新消息 ID；既有未读和更早消息不受影响。
- 新消息的 `MessageItem.IsUnread`、Core 未读、会话徽标和服务器标读流程都保持权威值。用户滚离底部、窗口转入后台或切换会话时，仍显示正常的新消息/未读提示并保护阅读位置。

## Deterministic validation

- 当前前台底部实时消息、重复状态投影、未读投影和消息视口聚焦回归：passed 4/4；最终输出位于各项目的 `.verify/stage37-active-incoming-tests-3/`。
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage37-active-incoming-build-2/` — passed with 0 warnings and 0 errors。

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Follow-up interaction correction — append must scroll, not snap

- The first check was substantially better but still looked like the avatar/list refreshed. The user clarified that the whole viewport snapped: existing messages should visibly move upward while the new bottom message enters.
- Message objects and collection rows were already reconciled in place. The remaining refresh impression came from `RealtimeFollow` using `ScrollTo(... animate: false)` or native `ChangeView(... disableAnimation: true)`, which instantly re-realized/repositioned the visible containers.
- An avatar-cache-first proposal was investigated and rejected after the clarification, then fully removed; it did not address the viewport behavior.
- Only `RealtimeFollow` now enables the native scroll animation. Conversation activation/reactivation, manual latest jump, message anchor and pagination retain their existing non-animated precise positioning.
- Scroll-animation policy plus active-conversation realtime regressions passed 6/6. The final App Debug build passed with 0 warnings/errors. User Visual Studio confirmation is pending.

## Follow-up defect — animated follow oscillated vertically

- The first animated build moved the existing rows upward correctly, but then scrolled up and down repeatedly.
- Three internal-only desktop captures taken about 300 ms apart confirmed that the same visible rows changed vertical positions and an older row re-entered at the top; the window and conversation did not reload.
- Root cause: each intermediate `LayoutUpdated`/`Scrolled` event retried the active `RealtimeFollow`, restarting `ScrollTo` or `ChangeView` while the previous animation was still running.
- A realtime request now issues at most one native animated scroll. Intermediate layout/scroll events only evaluate completion; activation and other non-animated requests retain their existing retry behavior.
- Animation-selection, single-issue and active-conversation realtime regressions passed 10/10. The final App Debug build passed with 0 warnings/errors. The captures remain temporary and are not repository artifacts; user Visual Studio reconfirmation is pending.

## Follow-up defect — new row and previous row animated in reverse

- The next Visual Studio check used existing messages A/B followed by incoming C. C appeared directly, while the previous B row was unrealized and shown again, so the visible entrance effect landed on the wrong message.
- Root cause: before C's native container existed, `RealtimeFollow` called item-level `CollectionView.ScrollTo(C, animate: true)`. Container realization/reuse occurred before the bottom movement and could re-present B instead of letting C enter naturally.
- `RealtimeFollow` now waits for a real bottom extent and animates the native `ScrollViewer` offset even while C's container is not yet realized. Conversation activation/reactivation keeps the existing non-animated item-level realization path.
- The focused viewport policy suite passed 26/26 and the isolated App Debug build passed with 0 warnings/errors. User Visual Studio reconfirmation is pending.

## Shortest manual check

1. 在 Visual Studio 启动后打开与对方的一对一会话并停在消息底部，让对方发一条消息；确认已有消息平滑向上滚动，新消息从底部进入，不出现未读分隔、“跳转到最新消息/加载最新消息”或整列刷新。
2. 向上滚离底部，再让对方发消息；确认界面不抢回底部，并出现正常的新消息提示。
3. 自己发送一条消息；确认原有发送与跟随效果不变。

## Manual result

- The user confirmed the final native-bottom-offset animation in Visual Studio and explicitly authorized a local commit.
