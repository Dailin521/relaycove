# Stage 24.3 — 私信缓存切换与消息视口稳定性

- Status: Full-validated merge commit `55a94d3` plus a Release-only native-preview compile follow-up; ready for user-authorized integration to `main`
- Starting point: `main@efcebfe` plus local commit `a7a0059`
- External effects: no Realm, Live or deployment; Git integration is user-authorized

## Scope

1. 私信 A→B→A 切换时先显示 Core 缓存窗口，不显示空白消息区。
2. 相同 pending 私信点击合并；不同会话仍取消过期选择。
3. 等价历史页不重复发布；导航/消息行保持稳定实例。
4. 程序化 bottom-scroll 与布局重排不触发错误的 older-page/prepend-anchor 行为。

## Verification

- Full passed: Debug and Release each Core 109/109, Zulip.Client 45/45, Data 23/23 and App 105/105 (282/282); Web typecheck, 86/86 unit tests, production build and both fake-HTTP Playwright runs (6 + 1) passed.
- ZIP SHA-256: `60EC1985E938EDFA1329FE64D625A63853E5510619B9073C435FF79EDB9D684B` (2026-08-17 14:21 CST). Release emitted one non-fatal `XC0022` compiled-binding optimization warning at `MainPage.xaml:318`; no build errors.
- Live and formal Windows Machine acceptance remain pending for this candidate.

## Gates

- 无真实 Realm、人工密码登录、真实长列表/缩放/高对比或干净 VM 验收。
