# Stage 24.7 — 频道与话题树形导航（工作日志）

- Status: delivered and pushed as `main@a750dcd`; no Visual Studio manual result was explicitly recorded
- Starting point: `main@2c85700`
- Date: 2026-08-19 CST
- Scope: `RelayCove.App` 频道/话题导航、App 确定性测试与交互文档；不修改 Core、Data、Zulip 协议、Web、Realm 配置或服务端
- Official reference: Zulip [`left_sidebar_stream_actions_popover.hbs`](https://github.com/zulip/zulip/blob/main/web/templates/popovers/left_sidebar/left_sidebar_stream_actions_popover.hbs) and [`stream_popover.ts`](https://github.com/zulip/zulip/blob/main/web/src/stream_popover.ts), read on 2026-08-19.

## 用户目标

将左侧频道区从带最近摘要的卡片改为紧凑树形导航：频道下直接展开话题；点击已展开频道时收起；频道行在选中、悬停或菜单打开时提供创建话题和更多操作。

## 最终实现

- `ChannelItem` 以频道 ID 对账并原地更新，保留 `IsSelected`、`IsExpanded`、悬停和菜单目标等可观察行状态，避免状态发布时丢失交互状态。
- 一次只展开一个频道。首次激活展开并异步读取权威话题，然后选择本次运行中记住的话题或最新话题；再次点击只收起，不改变当前聊天。切换频道通过既有导航 generation/cancellation 取消过期话题加载；切换私信收起频道树。
- 紧凑频道主行显示彩色 `#`、名称、未读和行内 `+`/更多。话题缩进在该行下，单话题也显示；当前话题有勾选与选中背景。
- 行内 `+` 打开既有新建频道话题对话框，并禁用频道选择器以固定到所点频道。取消或只打开草稿不会发起 Realm 写入；发送首条消息仍走原有发送流程。
- 更多弹层使用页面锚点、外点关闭、Escape、上下/Home/End 键导航与触发按钮焦点返回。按 Zulip 官方 `left_sidebar_stream_actions_popover.hbs` 的分段层级显示频道标题、动态读/未读、频道订阅、话题列表、复制链接、设置、置顶、静音、退出和修改颜色，全部以触发行的频道 ID 操作。话题列表展开并激活目标频道；复制链接写入本机剪贴板。频道设置在需要时先打开该频道的记住/最新话题，再显示既有详情面板；退出继续复用二次确认。

## 被否决或不在范围的方案

- 不保留频道卡片最近摘要和时间：它们与目标紧凑树形导航冲突。
- 不把频道级已读/未读、频道订阅页和颜色修改伪装为已接通：它们保留官方菜单位置，但当前 App 只给出“未接通、未执行 Realm 操作”的明确提示；不修改 Core、协议或 Realm 接口。
- 不引入新的持久化字段：频道展开仅是本次 App 运行状态。
- 不修改 Core、协议、Data、Web 或 Realm 接口。

## 确定性验证

```powershell
dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj `
  -c Debug --no-restore --nologo --verbosity minimal `
  -p:OutputPath=artifacts/local-test/stage24-7-channel-tree/
```

- App-only Debug build：通过，0 warning / 0 error。
- `RelayCove.App.Tests`：140/140 通过。覆盖零/单/多话题展开、再次点击收起而不换会话、旧频道加载取消、私信切换收起、行内 `+` 锁定正确频道且不选会话、菜单目标/动态文案/退出确认，以及目标频道的话题列表、复制频道链接和未接通功能的零写入提示。
- 未运行 Fast、Full、Live；除下述用户授权的离线副屏预览外，未启动、移动、操作或截图 MAUI 窗口；未执行 Realm 写入、部署或打包。

### 用户授权的 Debug 副屏预览

- 使用 `scripts/start-maui-preview.ps1 -Scene shell -Theme light -Width 1024 -Height 768` 启动离线 `NativeShellPreviewSession`；它仅使用内存预览数据，不连接 Zulip。脚本确认 `DISPLAY2` 为非主屏，窗口 PID `42004`，DPI 144 / 150%，截图尺寸 1536×1152 像素。
- `01-shell-light.png`（SHA-256 `F92E204BECB6A2FCBAB4D634C0C2B89DEBFA508FDE9C406F6CD0C1B5ABB8F1A7`）确认紧凑频道行以及选中 `design` 行的 `+`/更多按钮；不再显示频道卡片摘要和时间。
- 使用该预览窗口的 UI Automation InvokePattern（不注入鼠标、键盘或前台焦点）打开更多和行内 `+`：`02-channel-menu.png`（`26659AE8E3B3985FF05A40CDFFCD4CD7232EBC6957260DF0578DF9D51EE0C65A`）确认锚定菜单与四个原有动作；`03-new-topic-locked-channel.png`（`E3B32D8616DF52D5D6A050BD64C117DF6C1CDAEDE49CB9DBC3EEC36805556CA5`）确认新话题对话框锁定 `UI 设计讨论` 频道。随后 InvokePattern 执行“取消”，未填写主题或触发发送。
- 菜单的“前往话题列表”在副屏实际激活 `design` 并显示子话题：`13-topic-list-visible.png`（`F40F6AF57EDA87733AC47890827BBE7771D5B6A14F2519E2C479B90788939F79`）。它也在 UIA 中出现两个 `UI 设计讨论` 元素（列表子项与聊天标题）。`14-official-channel-menu-final.png`（`82A294B388D5B8CDF47A3593FA963DD6BFA2FD57929EBAE9409A39387F3793EC`）确认最终官方菜单层级。中途发现嵌套虚拟列表不会可靠重测量/绑定；最终采用受限高度 `ScrollView` 加频道行直接拥有的当前话题集合，已排除内层 `CollectionView` 方案。
- 预览频道行在 UIA 中仍仅暴露为无 Invoke/SelectionItem pattern 的 `ListItem`，因此没有通过全局鼠标/键盘注入模拟展开/收起；该路径由上述 140 条确定性测试及菜单驱动的实际展开显示覆盖，仍待用户在 Visual Studio 快速人工确认。预览不替代正式 Realm、Visual Studio 人工验收、Live、打包或干净 VM 门禁。
- 截图目录：`artifacts/maui/screenshots/stage24-7-channel-topic-tree/`。

## 待用户 Visual Studio 验证

1. 点击含话题频道展开、再次点击收起；切换频道/私信后确认旧话题不展开且不会覆盖当前聊天。
2. 点击话题后保持所属频道展开；单话题频道也能看到话题。
3. 悬停或选中频道行确认 `+` 和更多可见；点击 `+` 后确认频道已锁定，随后取消。
4. 打开更多菜单，确认官方分段、锚定位置、Escape/外点关闭和焦点返回；确认话题列表、复制链接和频道设置均针对所点频道。频道级读/未读和修改颜色应只显示未接通提示。
5. 本轮人工验收不要执行置顶、静音或退出，也不要发送首条消息，以避免真实 Realm 写入。

本阶段已按用户明确指令提交并推送为 `main@a750dcd`。工作日志只保留已记录的确定性测试和离线副屏预览，不补写未经明确记录的 Visual Studio 人工结果。
