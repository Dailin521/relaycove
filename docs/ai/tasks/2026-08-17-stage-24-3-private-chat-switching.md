# Stage 24.3 — 私信缓存切换与消息视口稳定性

- Status: Main merge candidate; narrow regression tests passed, Full pending
- Starting point: `main@efcebfe` plus local commit `a7a0059`
- External effects: no Realm, Live, deployment or push from this candidate

## Scope

1. 私信 A→B→A 切换时先显示 Core 缓存窗口，不显示空白消息区。
2. 相同 pending 私信点击合并；不同会话仍取消过期选择。
3. 等价历史页不重复发布；导航/消息行保持稳定实例。
4. 程序化 bottom-scroll 与布局重排不触发错误的 older-page/prepend-anchor 行为。

## Verification

- App: 105/105 passed.
- Core: 109/109 passed.
- Fast/Full/Live and formal Windows Machine acceptance remain pending for this candidate.

## Gates

- 无真实 Realm、人工密码登录、真实长列表/缩放/高对比或干净 VM 验收。
