# RelayCove v1 外层执行状态

> 本文件不定义产品范围或架构，也不替代任务验收。产品与架构真源依次为 [`RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md)、[`DECISIONS.md`](DECISIONS.md) 和当前活动任务；执行与证据规则以 [`WORKFLOW.md`](WORKFLOW.md) 为准。

## 当前快照

```text
ExecutionStatus: running
CurrentMilestone: M2
CurrentStage: 阶段 9
ActiveTask: docs/ai/tasks/2026-08-04-stage-9-attachment-download-cache.md
TaskStatus: completed
IntegrationBranch: agent/v1-integration
LatestGreenCodeCommit: 438f7f766e62aa4f73496cb564ed44e0fb35544b
LatestGreenIntegrationCommit: b678865e2f2a559e9e620567a5caacb4a5882ae0
NextAction: 仅快进集成后进入 WPF 附件展示、下载/取消/失败重试与安全打开
ClaudeCalls: 75（全部终态；#55/#58/#62 已主动停止，#56/#57/#59/#60/#61/#63/#64/#65/#66/#67/#68/#69/#70/#71/#73 失败，#72 中断且恢复失败；仅关键用途调用）
ClaudeCostUsd: 80.07301150 exact confirmed + 56.61 local CLI displayed（#44–#50/#74–#75）；按显示值合计约 136.68301150；另有四十次失败/中断调用费用 unavailable
Blocker: none
RequiredUserGate: none
```

`ExecutionStatus` 只允许以下值：

- `running`：仍在向 v1 RC 推进。
- `blocked`：同一真实阻塞满足工作流规定的连续审计条件，且无法继续做有意义的工作。
- `v1_rc_ready`：全部 `V1_RC_READY` 条件已有当前证据证明。
- `released`：真实环境验收通过，且用户已经明确授权合并、Tag、Release 与生产部署。

## 里程碑状态

| 里程碑 | 状态 | 当前证据 | 下一出口 |
| --- | --- | --- | --- |
| M0 | `completed` | 同步契约、`DEC-003`、解决方案和真实 Fast/Full 验证均通过 | 已进入 M1 |
| M1 | `completed` | 会话成员、权限、文字消息、History/Around/Sync、SignalR、账户隔离本地缓存与 Windows 日常聊天 UI 已形成绿色纵向闭环 | 已进入 M2 |
| M2 | `running` | 阶段 9 服务端附件上传/下载/授权、客户端元数据缓存、durable Image/File 上传发送、WPF 原生文件选择/进度与 FileDrop/PNG Ctrl+V，以及强 ETag 全量可信下载、账户隔离 cache 与原子本地状态 core 已完成 | Internal Alpha 附件下载/cache 交互与验收证据完整 |
| M3 | `pending` | 尚未开始 | Beta 验收证据完整 |
| M4 | `pending` | 尚未开始 | RC 自动化、包与发布材料完整 |
| M5 | `pending` | 尚未开始 | 自动验证完成；真实 Windows/VPS/双客户端 Gate 明确记录 |

里程碑顺序来自当前 v1 执行目标；每个里程碑的功能口径和最终交付标准仍由工程方案、决策记录和对应最小纵向任务冻结，本文件不预写实现细节。

## 集成与绿色状态

- `agent/v1-integration` 是本地最新绿色集成头；任务分支只有完成验证和交接提交后，才允许仅快进该分支。
- 当前管理员引导代码检查点 `419ef00069c86c85b097a7961cebe95a16730cc5` 已通过 Fast、Full、100 项测试、真实 bootstrap/动态管理员授权/同名并发、密码与日志边界及依赖漏洞审计；Claude 无候选结论，已如实降级记录 Codex 固定差异自审。
- 当前会话存储代码检查点 `1a3c49289940d625182237fddcd1954fc40983e9` 已通过 Fast、Full、116 项测试、真实 migration up/down/旧认证数据保留、约束/唯一/外键、model drift 与漏洞审计；Claude #20 无结论，已如实降级记录 Codex 固定差异复核。
- 当前会话访问与成员 API 代码检查点 `b9b004109183e0157bca5c16f0acdaf7a39c8940` 已通过 Full、134 项测试、真实 HTTP/SQLite Direct 与成员并发、动态授权/撤权、共享访问查询、单查询、busy 503、model drift 与漏洞审计；Claude #21 无结论，已如实降级记录 Codex 固定差异复核。
- 当前文字消息 API 代码检查点 `391aff08f3a48396e1a499e3e4cc9db2cc7fdc41` 已通过 Full、156 项测试、真实 SQLite AUTOINCREMENT/migration up-down/外键、顺序/并发/跨发送者幂等、撤权优先、单查询 keyset History、未读/入群水位、busy 503、model drift 与漏洞审计；Claude #22 无结论，已如实降级记录 Codex 固定差异复核。
- 当前 read-through API 代码检查点 `d92552a093a217d6c5c38b4ee27b89da542cc2b8` 已通过 Full、167 项测试、真实目标/权限优先、Public 首次状态与成员 API 隔离、Private/Direct 撤权、单调/并发、未读、busy 503、model drift 与漏洞审计；Claude #23 无结论，已如实降级记录 Codex 固定差异复核。
- 当前 around API 代码检查点 `6189e7f3db5536f3ec715fa4243b19cd60f5f6cd` 已通过 Full、181 项测试、专用双侧窗口、目标/mentions、Public/Private/Direct 权限、最终撤权重检、两条有限 SQL、日志、model drift 与漏洞审计；Claude #24 无结论，已如实降级记录 Codex 固定差异复核。
- 当前固定上界 Sync API 代码检查点 `9d87ac2ee94cc83005e15ca3b095dcc5ea8530dd` 已通过 Full、197 项测试、deferred 只读快照、固定上界/空洞前进、Public/Private/Direct 动态权限、mentions/两条查询、日志、model drift 与漏洞审计；Claude #25 无结论，已如实降级记录 Codex 固定差异复核。
- 当前 SignalR NewMessage 代码检查点 `5556899ca699bab097acae0003983943b4ca92d9` 已通过 Full、202 项测试、认证/query-token 日志边界、Public/Private/Direct 分组与当前收件人、完整 DTO、撤权旧连接、顺序/并发幂等、transport 故障隔离、model drift 与漏洞审计；Claude #26 无结论，已如实降级记录 Codex 固定差异复核。
- 当前 SignalR ConversationAccessRevoked 代码检查点 `709a2b5a6ccd54f2a293070998c6f98734ae3d93` 已通过 Full、206 项测试、多连接目标路由、其他用户隔离、撤权后生产 NewMessage 停止、并发/重复一次事件、负向零事件、transport 故障隔离、model drift 与漏洞审计；固定差异复核无剩余发现。
- 当前客户端 SignalR 代码检查点 `c3717c9455a98cbf9014e8cbd37ef2f635261cc3` 已通过 Full、220 项测试、真实认证 TestServer/LongPolling、反向代理子路径、动态 token、完整 DTO、撤权 FIFO 屏障、初始失败重启、自动重连状态、sink 故障隔离、并发生命周期和回调内 Stop/Dispose；关键竞争测试 Release 连续 5 轮通过，model drift 与漏洞审计无异常，Claude #29 无结论且已降级 Codex 固定差异复核。
- 当前账户隔离本地缓存代码检查点 `9182c73b79aa9ec6fd09bd681d6e9aa19ccd35f0` 已通过 Full、244 项测试、真实磁盘 scope/合并/重启/撤权/故障/竞争/日志、关键竞争 Release 连续 5 轮、原生 SQLite 安全版本、model drift 与八项目漏洞审计；Claude #30 有效挑战已落实为 `DEC-018`，首次审计发现的 High 依赖已由 `DEC-019` 修复并复验。
- 当前客户端 Complete 会话快照与 Sync 页原子提交代码检查点 `cb7b1ed26dbbb934d92865af829beb159370abcf` 已通过 Full、259 项测试、真实磁盘快照对账/durable intent/重新加入/整页回滚/游标重启/账户隔离、关键故障与竞态 Release 连续 5 轮、model drift 与八项目漏洞审计；Claude 已达 `30/30` 硬上限，按账本使用 Codex 固定差异复核。
- 当前客户端 Sync HTTP 编排代码检查点 `8f7838baa79f194702cd88d3d4f6134d5f6e9341` 已通过 Full、285 项测试、真实磁盘 + 可控 HTTP 多页/重试/Retry-After/refresh/409 block/single-flight/取消/日志场景，关键 5 项竞态 Release 连续 10 轮、model drift 与八项目漏洞审计；Claude 已达 `30/30` 硬上限，按账本使用 Codex 固定差异复核。
- 当前客户端认证会话代码检查点 `821d8598c8936376ba31e586bd8cfd4d23beda40` 已通过 Full、322 项测试、真实 login 请求与状态分类、响应/Bearer 校验、refresh single-flight rotation、logout/Dispose 线性化、取消与日志脱敏场景，关键 5 项竞态 Release 连续 10 轮、model drift 与八项目漏洞审计；Claude 已达 `30/30` 硬上限，按账本使用 Codex 固定差异复核。
- 当前 DPAPI 客户端凭据存储代码检查点 `82267b785fa6ef7d04de4906b9b01de0e0cfda54` 已通过 Full、339 项测试、真实 Windows CurrentUser DPAPI、ciphertext 明文扫描、轮换原子替换/失败保旧、并发/取消、篡改/截断/超限/非法 payload、清除与日志脱敏场景，关键 5 项文件竞态 Release 连续 10 轮、model drift 与八项目漏洞审计；Claude 已达 `30/30` 硬上限，按账本使用 Codex 固定差异复核。
- 当前持久 refresh 会话恢复代码检查点 `5dece6b577734649ef75f36c68ea25ec82b08703` 已通过 Full、362 项测试、启动单次恢复/身份校验/轮换落盘、无效 2xx fail-closed、保存与清理失败、logout 条件性 revoke、旧会话所有权门和日志脱敏场景，关键 9 项 Release 连续 10 轮、model drift 与八项目漏洞审计；Claude 已达 `30/30` 硬上限，按账本使用 Codex 固定差异复核。
- 当前单账户 runtime 代码检查点 `e2195cd6835b9d858c00f1757e7fc7d640ea1021` 已通过 Full、382 项测试、真实 cache/HTTP/DPAPI、先连接后同步、连接失败仍补拉、显式 flight/启动终止收敛、Dispose 保留与 Logout 清除凭据、账户切换所有权和日志脱敏场景；组合回归 94/94、关键竞态 200/200、model drift 与八项目漏洞审计通过。Claude #32 的有效发现已修正并复验，实际模型偏差已如实记录。
- 当前本地未读与通知候选代码检查点 `c6955cb649d16b8a6d488dd228f99747a8c8c64c` 已通过 Full、413 项测试、真实 SQLite 来源/前台/本人/pending/重复/History、权威列表落后与 Realtime/Sync/History 乱序、已读边界下旧行、损坏游标 fail-closed、Sync 空洞回填/冲突整页回滚、账户隔离和 runtime 组合；10,000 条历史 + 200 条前台页整项 195 ms，新增三条边界回归连续 10 轮 30/30，既有关键竞态 40/40、model drift 与八项目漏洞审计通过。Claude #33–#35 的有效发现均已由 Codex 复算、修正并本机复验。
- 当前 read-through 安全上传代码检查点 `8384e6166d69467377e36efa549309f891822076` 已通过 Full、447 项测试、会话内真实已读目标/全局 cursor 反例/未读空洞、102 会话分页、receipt 双权威清理、401/状态分类/快照级抑制、稳定撤权、损坏行隔离、busy、single-flight/补跑、重启/取消/Dispose 与 runtime 接线；协调器 31 项 Release 连续 10 轮 310/310，model drift 与八项目漏洞审计通过。Claude #36–#37 的有效发现均由 Codex 复算并在最终代码检查点收敛，实际模型偏差已如实记录。
- 当前平台无关通知协调器代码检查点 `92b924f2ee3dd45e25ee0c9ff3358b346b38f3a8` 已通过 Full、493 项测试、旧状态原子收养、权威静音、显式候选复核、at-least-once Recovery、generation round gate、所有撤权来源、durable 平台清理确认和 runtime 终止接线；通知/撤权定向集 Release 连续 10 轮 460/460，model drift 与八项目漏洞审计通过。Claude #38–#39 的有效发现均由 Codex 复算并在最终代码检查点收敛，实际模型偏差已如实记录。
- 当前 Windows 原生通知平台代码检查点 `bb4ae92dbdc1332ecc7283619b78567b44a62f04` 已通过 Full、555 项测试、通知定向集 830/830、最终 host/platform 修复集 320/320、安装态 production builder payload/Register/Show/GetAll/Remove、WPF 非阻塞启停、model drift 与八项目漏洞审计；Claude #40–#42 的有效发现均由 Codex 复算、修正并在本机门禁收敛，三次终态实际模型偏差已如实记录。
- 当前单实例激活代码检查点 `b8589669e6015b884f171456cc5d34fd402e4212` 已通过 Fast/Full 600 项测试、Client 389/389、activation 60/60 与压力 600/600、真实优雅交接 30 轮×10 竞争者、冷/运行中/交接后原生 COM callback、并发冷启动/继任者/强杀恢复、model drift 与八项目漏洞审计。固定 AppInstance key、单次当前读取、完整 redirect、授权路由和通知注销后释放 key 已冻结为 `DEC-030`；Claude #43–#44 的有效发现经 Codex 复算、修正和本机复验，实际账户/UI 接线保持阶段 8 `未验证`。
- 当前桌面 attention 与托盘生命周期最终代码检查点 `93e4740e69049d97d4f9d0871862d80fecb8e740` 已通过 Fast/Full 629 项测试、Client 418/418、桌面/通知定向 Release 280/280、复审补丁定向 39/39、安装态静音 Toast payload/Register/Show/GetAll/Remove、极早 WM_CLOSE 隐藏、次实例同 HWND 恢复、NotifyIcon 真实 Exit、MessageBeep/FlashWindowEx Start/STOP、model drift 与八项目漏洞审计。同步轮共享 gate、Toast 静音、STOP 所有权和 tray → notification unregister → AppInstance key 顺序冻结为 `DEC-031`；Claude #45 的有效发现均由 Codex 复算修正，#46 固定提交复审 `PASS` 且四项非阻断 P2 已在最终检查点收敛。真实账户/UI 接线、隐藏托盘时不可见的任务栏闪烁与系统注销/关机实机探针保持明确限制。
- 当前 production 账户壳最终代码检查点 `93d8fd5883a45ef154ef4612da7e7fcb8b9f6dc7` 已通过最终 Fast/Full 658 项、Client 447/447、review-fix 定向 71/71 与 60/60 重复、Client 完整套件 SQLite 隔离检查点 2,205 次及 review-fix 检查点 2,230 次、真实 Release WPF 登录/不可达失败/密码清空/关闭隐藏/同 HWND 恢复、model drift 与八项目漏洞审计。单一账户所有权、权威缓存后 activation lease、detach 依赖、凭据 clear barrier、清理失败可见文案与 WPF live region 冻结为 `DEC-032`；Claude #47–#50 有效发现均由 Codex 复算、修正和本机复验。真实服务器双客户端、持续连接/总未读、Narrator 播报、目录整体只读/ACL deny、隐藏托盘闪烁与系统注销/关机仍保持明确限制。
- 当前账户隔离会话列表最终代码检查点 `ea83e7bf37e83f03c678bf0f82375bfb8a4166af` 已通过最终 Full 673 项、Client 462/462、会话列表/状态/runtime/coordinator 定向 41/41 与 410/410 重复、完整 Client 2,310 次、真实 Release WPF 响应窗口/单实例/进程清理、model drift 与八项目漏洞审计。权威门控读取、精确预览 join、提交后信号、dirty single-flight、版本化旧 runtime 隔离、真实总未读/持续连接和选择不提前推进已读冻结为 `DEC-033`；Claude #51–#53 的成立意见均由 Codex 复算并在最终代码收敛，三次终态模型偏差如实记录。真实登录列表视觉、SignalR/通知点击/托盘数字与 Narrator 保持后续 UI/M5 Gate。
- 当前有界消息列表最终代码检查点 `46a59f6482263cdbf9a1d12a1470aa79bdff6960` 已通过最终 Fast/Full 704 项、Client 493/493、cache/History/Around/coordinator/滚动关键集 81/81 与 810/810 重复、真实 Release WPF 响应窗口/单实例/进程清理、model drift 与八项目漏洞审计。有界只读页面、History/Around 原子 merge、稳定撤权、版本化选择、虚拟化滚动和已应用视口后的精确 read-through 冻结为 `DEC-034`；Claude #54 的成立意见与 #56 额度失败前两条部分意见均由 Codex 独立复算并在最终代码收敛，失败任务未冒充终局审查。真实登录消息、双客户端、通知定位与 Narrator 保持后续 UI/M5 Gate。
- 当前 Text 发送最终代码检查点 `4cad2b3769eb555f009f3f3eaf1e93b2c642a0c6` 已通过最终 Fast/Full 743 项、Client 532/532、发送/pending/回声/coordinator/选择关键集 250/250 重复、真实 Release WPF 非零窗口句柄/响应/单实例/进程清理、model drift 与八项目漏洞审计。严格 Text 验证、durable pending、单次幂等 POST、一次 401 refresh、稳定撤权、响应/回声同一行提升、显式原键重试和 nullable 服务端身份冻结为 `DEC-035`；Claude #57–#60 均未形成审查结论，Codex 固定差异自审补出的发送者校验和同进程恢复竞态已在最终代码收敛。真实账户/VPS/双客户端发送视觉与 Narrator 保持 M5 Gate。
- 当前 WindowActivated / Periodic Sync 最终代码检查点 `64f6985a48f1aaeec48af36bd64f17d87b0f8341` 已通过最终 Fast/Full 751 项、Client 540/540、scheduler/runtime/既有 Sync coordinator 关键集 670/670 重复、真实 Release WPF 非零窗口句柄/响应/单实例/进程清理、model drift 与八项目漏洞审计。Startup 后前台上升沿、五分钟完成后节拍、既有 single-flight 复用和旧 scope 终止顺序冻结为 `DEC-036`；Claude #61–#62 均未读取代码或形成结论，Codex 固定提交自审无剩余发现。真实丢推送、五分钟壁钟与 VPS/双客户端保持 M5 Gate。
- 当前消息复制与日期分割最终代码检查点 `ab564319aa39a160f63de54fc03e8dce23f339d5` 已通过最终 Fast/两次 Full 766 项、Client 555/555、presentation/Copy/Clipboard writer 关键集 80/80、真实 Release WPF 响应窗口/单实例/清理、model drift 与八项目漏洞审计。当前 Ready 快照成员门、逐字 Unicode Copy、Clipboard 占用可恢复分类及绝对本地日期分组已收敛；自动化刻意不覆盖用户真实 Clipboard。链接、`@用户`、新消息分割线与真实登录视觉保持后续/M5 Gate。
- 当前安全链接最终代码检查点 `018df9840fcb35a6a5ee41b21191b129792a2f2f` 已通过最终 Fast/两次 Full 782 项、Client 571/571、parser/policy/launcher/presenter 关键集 190/190、真实 Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计、敏感日志检索与空白检查。有界绝对 HTTP(S) 识别、当前 Ready 快照成员门和无命令拼接的 Windows shell association 已收敛为 `DEC-038`；真实浏览器打开刻意不自动化，`@用户`、新消息分割线与真实登录视觉保持后续/M5 Gate。
- 当前新消息分割线最终代码检查点 `49d72a3f882c2ea450010bec83a0d18e30a02d26` 已通过最终 Fast/两次 Full 788 项、Client 577/577、cache/shell/presenter 关键集 520/520、真实 Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计与空白检查。同事务打开状态、selection 冻结和 History/Around 分页证明已收敛为 `DEC-039`；真实登录数据下视觉/Narrator 保持 M5 Gate，`@用户` 仍需先冻结普通用户目录协议。
- 当前会话作用域提及候选最终代码检查点 `2b8a3a1d9896dd241c35d164a2e6304c33df075b` 已通过最终 Fast/两次 Full 807 项（Shared 37、Server 192、Client 577、Updater 1）、Shared/validator/真实 HTTP/SQLite endpoint 关键集 190/190、model drift、八项目漏洞审计与空白检查。最小脱敏响应、与发送授权同构的 Public/Private/Direct 查询、字面前缀、稳定 limit+1 与零结果撤权复核已收敛为 `DEC-040`；客户端 picker/token/durable 发送留在下一切片。
- 当前客户端提及组合与可靠发送代码检查点 `82c083d5fa319ea2cb2fe8fc51ba09c5382c8a1c` 已通过最终 Fast/三次 Full 860 项（Shared 37、Server 192、Client 630、Updater 1）、提及/发送/shell 关键集 740/740、Release WPF 响应窗口/单实例/精确清理、model drift、八项目漏洞审计、敏感日志检索与空白检查。候选 selection gate、正文 token 存活条件、pending 前规范 ID 快照和重启/显式 retry 原集合已收敛为 `DEC-041`；无登录会话下 picker UIA 不适用，真实登录视觉/键盘/Narrator 留到 M5。
- 当前服务端安全附件上传代码检查点 `a2ef8a72f24829e61f5ae8e34aa3b661ce90fd0d` 已通过最终 Fast/两次 Full 911 项（Shared 39、Server 241、Client 630、Updater 1）、Attachment Release 定向集 390/390、真实 Kestrel exact-limit 201/宿主级稳定 413/不透明字节一致落盘、migration up/down、冲突回滚与启动恢复、model drift、八项目漏洞审计、敏感日志检索和空白检查。多层有界 streaming、非公开随机文件、commit 前发布与严格崩溃恢复已收敛为 `DEC-042`；消息绑定、下载授权、客户端、长期未绑定回收和 VPS 保持后续边界。
- 当前服务端附件消息/下载代码检查点 `41f7d11e207fd984bbc3e2a8c003f9bf2ed6a2e9` 已通过最终 Fast/两次 Full 924 项（Shared 39、Server 255、Client 630、Updater 1）、Attachment/Message Release 定向集 960/960、真实 Kestrel 的发送 201/重放 200/载荷冲突 409/未绑定 403/完整下载 200/Range 206/匿名 401、lease DB/file/cancel 故障、model drift、八项目漏洞审计、敏感日志检索和空白检查。INSERT 后 owner/null 条件 attach-once、当前会话授权下载和 DB-first 未绑定 lease 已收敛为 `DEC-043`；客户端与 VPS 保持后续边界。
- 当前客户端附件元数据入库代码检查点 `53a5b630140517aca6be4bc0a4f43397857a0154` 与最终测试头 `722ad495fc216e3a5ddb88779045274bb7bbf898` 已通过最终 Fast/两次 Full 932 项（Shared 39、Server 255、Client 641、Updater 1）、Client 附件 Release 定向集 990/990、真实 SQLite v1→v2/提交回滚/v3 拒绝/消息整笔回滚/并发重复/撤权级联/账户隔离、全部协议入口、model drift、八项目漏洞审计、敏感日志与空白检查。严格附件 DTO 与相对路由、消息同事务持久化、完整回读和 fail-closed 已收敛为 `DEC-044`；上传/发送、内容下载、UI 与 VPS 保持后续边界。Claude #72 实际 Opus 只读取证但无正式结论，已如实降级为 Codex 固定差异复核。
- 当前 WPF 附件组合代码检查点 `4c8a032e46e36a9c0bb0e3cc2d9d2d21c020c037` 已通过最终 Fast/Full 1045 项（Shared 39、Server 255、Client 750、Updater 1）、附件/发送/shell 定向 176/176、原子文件 source/分类/变化/脱敏、部分批次/稳定 401/取消/pending/context-draft 回归、真实 Release WPF 响应窗口/单实例/精确清理和空白检查。路径内存边界、exact composer 草稿门、真实 content-copy 进度与 pending 恢复语义已收敛为 `DEC-046`；Codex reviewer 首轮两项 P2 修正后二轮无 P0/P1/P2。拖拽/截图、下载内容 UI、真实账户视觉/Narrator 与 VPS/双客户端保持后续或 M5 Gate。
- 当前 WPF 附件拖拽/截图输入生产代码检查点 `c3a018704c62294a5bd7268656981c073e25cebe` 已通过最终 Fast/Full 1088 项（Shared 39、Server 255、Client 793、Updater 1）、附件/Clipboard 定向 132/132、exact FileDrop/Copy、exact Ctrl+V 文本优先/repeat、STA 像素快照、25/100 MiB 双预算、取消/WIC 分类、精确私有 buffer、source-neutral draft、真实 Release WPF 响应窗口/单实例/精确清理、format 与空白检查。两路 Codex 最终复核无剩余 P0/P1/P2；Claude #74 的成立问题经 Codex 复算修正并本机复验，输入边界已收敛为 `DEC-047`。真实登录下 Drop/Ctrl+V 视觉/键盘/Narrator、下载/cache UI 与 VPS/双客户端保持后续或 M5 Gate。
- 当前附件可信下载/cache 生产代码检查点 `438f7f766e62aa4f73496cb564ed44e0fb35544b` 已通过最终 Fast/Full 1154 项（Shared 39、Server 255、Client 859、Updater 1）、Client attachment/cache/Kestrel 65/65、Server Attachment 51/51、真实动态端口 Kestrel 的 login/upload/send/授权 GET→客户端磁盘→SQLite、publish→CAS fault/restart、跨 cache 撤权/locked staging、损坏 final 与共享 quota、model drift、八项目漏洞审计、日志脱敏与空白检查。两路 Codex 最终复核均无剩余 P0–P2；Claude #75 的成立问题经 Codex 复算修正并本机复验，下载边界已收敛为 `DEC-048`。WPF 下载视觉、安全打开、缩略图/原图与 VPS/双客户端保持后续或 M5 Gate。
- `LatestGreenCodeCommit` 只记录已经通过任务要求的真实源代码提交；后续若验证失败，不得推进该值或集成分支。
- 用户已明确预授权绿色任务 push、仅快进合入集成分支、任务分支清理，以及在对应 Gate 条件真实满足后的 `main` 合并、Tag/Release、真实发布和生产部署，均无需二次确认；未满足 Gate 时不得提前执行。

## Claude 使用账本

| # | 日期 | 任务 | 类型 | 请求模型/档位 | 结果 | `cost_usd` |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 2026-08-03 | 同步契约 | 前置 challenge | Opus / XHigh | CLI 认证源冲突，未返回 `workspace_root`、实际模型或 `model_mismatch`；调用前后仓库状态一致 | `unavailable` |
| 2 | 2026-08-03 | 同步契约 | 候选 review | Opus / XHigh | 同一认证错误；固定 `ReviewHead` 未变化，降级 Codex 复核通过 | `unavailable` |
| 3 | 2026-08-03 | 认证共享契约 | 前置 challenge | Opus / XHigh | 同一认证错误；`ChallengeHead=6d60f9ae22a392adb75970763f260b10e53ebdbc` 与干净状态未变化，降级 Codex 反证 | `unavailable` |
| 4 | 2026-08-03 | 认证共享契约 | 候选 review | Opus / XHigh | 会话在返回前中断，无结果元数据；固定 `ReviewHead=9a867323095c9753c96cf55985396229d9088059` 未变化 | `unavailable` |
| 5 | 2026-08-03 | 认证共享契约 | 候选 review 重试 | Opus / XHigh | `claude_second_brain` MCP 仍因旧认证环境失败；未返回模型、workspace、mismatch 或费用 | `unavailable` |
| 6 | 2026-08-03 | 认证共享契约 | 只读 CLI 回退 | Opus / XHigh | 实际 `claude-opus-5`；在形成结论前触及预算，`terminal_reason=budget_exhausted` | `$0.5187985` |
| 7 | 2026-08-03 | 认证共享契约 | 只读 CLI 候选 review | Opus / XHigh | `workspace=E:\WorkSpace\RelayCove`（CLI 限域）、实际 `claude-opus-5`、mismatch=`false`、固定 ReviewHead 不变；`FIX_REQUIRED`，发现已修正 | `$0.666895` |
| 8 | 2026-08-03 | 认证共享契约 | 只读 CLI 定向复审 | Opus / XHigh | `ReviewHead=836d1e223d2cd9026fdf935be9cb16affbf45cf8`、实际 `claude-opus-5`、mismatch=`false`；五项原发现关闭，`PASS`；新增非阻塞 P3 已本地修正验证 | `$0.14818675` |
| 9 | 2026-08-03 | 认证存储 | 前置 challenge | Opus / XHigh | `claude_second_brain` 仓库只读调用在 300 秒工具上限被截断，无结构化结果 | `unavailable` |
| 10 | 2026-08-03 | 认证存储 | 只读 CLI challenge | Opus / XHigh | 仓库限域 CLI 在 300 秒外层上限被截断，无终局结果 | `unavailable` |
| 11 | 2026-08-03 | 认证存储 | 无仓库 MCP challenge | Opus / XHigh | 已提供仓库事实但仍在 300 秒 MCP 上限被截断，无结构化结果 | `unavailable` |
| 12 | 2026-08-03 | 认证存储 | 无工具 CLI challenge | Opus / XHigh | 实际 `claude-opus-5`、mismatch=`false`、提供事实对应 `ChallengeHead=6b821f1e9ba23b005630a3781fd407737e579684`；`REVISE`，有效发现纳入 `DEC-005` | `$0.3419475` |
| 13 | 2026-08-03 | 认证存储 | 只读 CLI 候选 review | Opus / XHigh | `ReviewHead=134fea6ceca3ec40aa8e5ce7e35a66eb1ba83d9a`；600 秒外层上限前无终局结果，本地降级复核发现并修复时间精度、CHECK 与用户名不变量缺口 | `unavailable` |
| 14 | 2026-08-03 | 认证存储 | 最终 MCP review | Opus / XHigh | `claude_second_brain` 接单后因 wrapper 认证源优先级导致 claude.ai connector 被禁用，未返回结构化模型、workspace 或费用 | `unavailable` |
| 15 | 2026-08-03 | 认证存储 | safe-mode 只读 CLI 最终 review | Opus / XHigh | 显式工作目录 `E:\WorkSpace\RelayCove`，工具限于 `Read/Glob/Grep`；实际 `claude-opus-5`、请求模型无偏差，`Base=0e5eefb..ReviewHead=6b0c85e`；耗时 `782578 ms`，`PASS`，无阻塞发现 | `$2.35148075` |
| 16 | 2026-08-03 | 认证端点 | safe-mode 只读 CLI challenge | Opus / XHigh | `ChallengeHead=87ff08aa5c7258a0b48cd37733a9b6fcd1d0b8d9`；实际 `claude-opus-5`，返回 `REVISE`；确认毫秒时钟和并发轮换阻塞项，中段本地输出被截断 | `$0.82506775` |
| 17 | 2026-08-03 | 认证端点 | safe-mode 只读 CLI 定向 challenge | Opus / XHigh | 同一 ChallengeHead，实际 `claude-opus-5`；完整返回 7 项 `REVISE` 修正，经本地代码与 Microsoft.Data.Sqlite 事务证据核对后纳入 `DEC-006` | `$0.57057225` |
| 18 | 2026-08-03 | 认证端点 | 最终 MCP review | Opus / XHigh | `ReviewHead=b72194a`；本机 `ANTHROPIC_API_KEY`/其他认证源优先于 claude.ai 登录，connector 被禁用，未返回模型、workspace、费用或审查结论；按用户要求未重复耗时调用，降级 Codex 固定差异自审 | `unavailable` |
| 19 | 2026-08-03 | 管理员引导 | 前置 challenge | Opus / XHigh | `ChallengeHead=0480b01`；120 秒上限后因同一认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库、NIST 与 Microsoft 官方证据收敛 `DEC-007` | `unavailable` |
| 20 | 2026-08-03 | 会话成员存储 | 前置 challenge | Opus / XHigh | `ChallengeHead=cd90023`；120 秒窗口内因本机认证源优先级禁用 claude.ai connector 而失败，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库与 Microsoft EF Core 官方证据收敛 `DEC-008` | `unavailable` |
| 21 | 2026-08-03 | 会话访问与成员 API | 前置 challenge | Opus / XHigh | `ChallengeHead=22d60cf`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库与 Microsoft SQLite/EF/资源授权官方证据收敛 `DEC-009` | `unavailable` |
| 22 | 2026-08-03 | 文字消息 API | 前置 challenge | Opus / XHigh | `ChallengeHead=e677597`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库与 SQLite/EF Core 官方证据收敛 `DEC-010` | `unavailable` |
| 23 | 2026-08-03 | read-through API | 前置 challenge | Opus / XHigh | `ChallengeHead=9c8211f`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库事务、权限与现有模型证据收敛 `DEC-011` | `unavailable` |
| 24 | 2026-08-03 | around API | 前置 challenge | Opus / XHigh | `ChallengeHead=acbd34b`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库协议、授权与不可变模型证据收敛 `DEC-012` | `unavailable` |
| 25 | 2026-08-03 | 固定上界 Sync API | 前置 challenge | Opus / XHigh | `ChallengeHead=0c7c767`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合冻结协议、本地 Microsoft.Data.Sqlite API 与当前模型证据收敛 `DEC-013` | `unavailable` |
| 26 | 2026-08-03 | SignalR NewMessage | 前置 challenge | Opus / XHigh | `ChallengeHead=cb5a4d6`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时；调用前后 HEAD 与干净状态不变，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库事务/权限与 ASP.NET Core 10 官方证据收敛 `DEC-014` | `unavailable` |
| 27 | 2026-08-03 | SignalR ConversationAccessRevoked | 前置 challenge | Opus / XHigh | `ChallengeHead=ca6fec7`；60 秒窗口内因本机认证源优先级禁用 claude.ai connector 而超时；调用前后 HEAD 与干净状态不变，无模型、workspace、费用或结论；按用户要求未重试，由 Codex 结合仓库事务/撤权与用户路由证据收敛 `DEC-015` | `unavailable` |
| 28 | 2026-08-03 | SignalR ConversationAccessRevoked | 本机后台只读 CLI review | Opus / XHigh | 从 `E:\WorkSpace\RelayCove` 启动且工具限于 `Read/Glob/Grep`；实际主要模型 `claude-opus-5`（CLI 含少量 `claude-sonnet-5` 开销），返回未提供 `workspace_root/model_mismatch`；约 `290742 ms` 后 `terminal_reason=budget_exhausted`，未形成 verdict/findings，不能标记通过，固定候选由 Codex 复核 | `$1.0153275` |
| 29 | 2026-08-03 | 客户端 SignalR 接收与连接状态 | 前置 challenge | Opus / XHigh | `ChallengeHead=8c811cf`；60 秒内 `claude_second_brain` MCP wrapper 仍因本机认证源优先级禁用 claude.ai connector 而超时；无模型、workspace、费用、结论或发现，不重试，由 Codex 结合仓库与 ASP.NET Core 10 官方证据收敛 | `unavailable` |
| 30 | 2026-08-03 | 客户端账户隔离缓存与撤权 | 本机后台无工具 challenge | Opus / XHigh | 实际主要模型 `claude-opus-5`（CLI 含少量 `claude-sonnet-5` 开销），约 `274623 ms` 后返回 `REVISE`；有效发现推动 durable revocation intent、冷启动默认隐藏旧缓存、读写双门禁、固定唯一键判定和取消不可丢撤权，纳入 `DEC-018` 与磁盘测试；未返回 `workspace_root/model_mismatch` | `$0.272205` |
| 31 | 2026-08-03 | 单账户 runtime | 项目内旧 0.3 候选 review | Opus / XHigh | 项目 `.codex` 仍遮蔽全局 0.5，只暴露同步 `consult_claude`；RPC 300 秒断开且旧实现未持久化任务，无结构化结论、模型或费用 | `unavailable` |
| 32 | 2026-08-03 | 单账户 runtime | 全局 0.5 持久只读 review | Opus / XHigh | job `8f89e113-84d0-4d83-908e-62dd07e88c77`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，872268 ms 后 `REVISE`。有效发现落实为 retry 失败仍补拉、显式 flight 线性化和在飞行终止测试，其余建议按当前可达路径/非目标裁定 | `$2.3230465` |
| 33 | 2026-08-03 | 本地未读与通知候选 | 全局 0.5 持久 challenge | Opus / XHigh | job `3f2699b1-9e8a-4845-b242-c74016360fa3`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，593013 ms 后返回。有效发现促成连续已读边界不越过已提交 cursor、显示名移出不可变冲突和后续旧缓存收养要求；其余超出本切片建议按非目标记录 | `$2.31851075` |
| 34 | 2026-08-03 | 本地未读与通知候选 | 全局 0.5 持久候选 review | Opus / XHigh | job `7f77374f-6c0d-4872-9912-0dd721930c32`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，1112066 ms 后返回。P2 权威覆盖反例和页内 read-through 写放大经 Codex 复算确认，落实为互斥区间派生、独立内存权威边界、每会话一次 read-through 与 10,000 行测试 | `$3.916135` |
| 35 | 2026-08-03 | 本地未读与通知候选 | 固定提交窄复审 | Opus / XHigh | job `d74f75d8-b3cf-4a16-8985-2467f2801b5d`，`workspace=E:\WorkSpace\RelayCove`，预期 `ReviewHead=4626f5628744837c63621f4e8fbbee95cb9cb0bc`；实际 `claude-sonnet-5`、`model_mismatch=true`，995274 ms 后 `FIX_REQUIRED`。只读 workspace 同时观察到后续 History 修复；P2 前台 read-through 重复扣减及两条 P3 经 Codex 复算成立并在 `c6955cb` 修正，Full 与边界回归通过 | `$4.279979` |
| 36 | 2026-08-03 | read-through 安全上传 | 全局 0.5 持久 challenge | Opus / XHigh | job `19595f3f-9c68-4363-a880-5caf0608ea4e`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，1073718 ms 后 `REVISE`。确认会话真实目标、空洞保护和 receipt 清理方向；永久错误重复发送与 busy 令 scope fatal 两项经 Codex 复算并在 `b70e645` 修正 | `$4.9180395` |
| 37 | 2026-08-03 | read-through 安全上传 | 全局 0.5 固定候选 review | Opus / XHigh | job `1cd74c21-0b8f-4e0a-981a-adb256a72dac`，`workspace=E:\WorkSpace\RelayCove`，`ReviewHead=b70e645`；实际 `claude-sonnet-5`、`model_mismatch=true`，1325318 ms 后 `FIX_REQUIRED`，无 P0/P1。三个 P2——`DEC-026` 漂移、撤权与批次读取竞争、损坏 pending 令全 scope fatal——经 Codex 复算成立并在 `8384e61` 与 `DEC-027` 修正，最终本机回归通过 | `$4.49239625` |
| 38 | 2026-08-03 | 平台无关通知协调器 | 全局 0.5 持久 challenge | Opus / XHigh | job `b72844be-9c07-4dde-83e6-89e5fd7d5e4a`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，631550 ms 后 `REVISE`。有效建议落实为平台三态、round `try/finally`、generation、非持久 Recovery cursor 与收养事务；平台不可用策略边界由 Codex 另行发现并修正 | `$4.1219325` |
| 39 | 2026-08-03 | 平台无关通知协调器 | 全局 0.5 最终 review | Opus / XHigh | job `0fff90fa-d3e1-4784-ba27-642241cbf7a3`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，2265022 ms 后返回。历史 tombstone 每轮重放与取消轮丢弃 Realtime-first 两项阻断经 Codex 复算成立并在 `92b924f` 修正；真实平台挂起 liveness 留作阶段 7 硬门禁，其余建议按当前可达路径裁定 | `$7.83017175` |
| 40 | 2026-08-03 | Windows 原生通知平台 | 全局 0.5 持久 challenge | Opus / XHigh | job `b8d105e8-48c5-434d-9ebe-34f4de55f450`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，797500 ms 后返回。有效发现落实为可恢复平台分类、Summary 聚合与清理、同步 Show/设置隔离和有界等待、惰性 manager 与下一单实例切片边界 | `$2.50882625` |
| 41 | 2026-08-03 | Windows 原生通知平台 | 全局 0.5 最终 review | Opus / XHigh | job `2c9aff25-7dd5-4919-bbdb-fc15af41f5cf`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-sonnet-5`、`model_mismatch=true`，924598 ms 后 `FIX_REQUIRED`。原生 launch 分隔符、注册就绪、同步移除隔离与不确定提交/撤权竞争经 Codex 复算成立并在 `5c244b0` 修正；其余按既定恢复策略和阶段 11 bootstrap 边界裁定 | `$3.4250045` |
| 42 | 2026-08-03 | Windows 原生通知平台 | 全局 0.5 窄范围复审 | Opus / XHigh | job `5ed116ad-1250-4422-84bc-30c939da40a6`，`workspace=E:\WorkSpace\RelayCove`；终态实际 `claude-sonnet-5`、`model_mismatch=true`，597806 ms 后确认五个核验点成立且无 P0/P1。迟到精确清理失败后的全局静默 P2 与持锁内联迟到注销 P3 经 Codex 复算成立并在 `bb4ae92` 修正，最终本机门禁通过 | `$1.9133445` |
| 43 | 2026-08-04 | 单实例激活与授权路由 | 全局 0.5 持久 review | Opus / XHigh | job `9f80d244-9aed-499b-bc4b-a7423cdd7fc1`，`workspace=E:\WorkSpace\RelayCove`；终态实际 `claude-sonnet-5`、`model_mismatch=true`，596834 ms 后 `REVISE`。当前读取者所有权、shutdown handoff、认证/权威快照门、pending 与去重发现经 Codex 复算并在最终代码收敛 | `$3.02272975` |
| 44 | 2026-08-04 | 单实例激活与授权路由 | 本机 Claude Code 2.1.220 后台最终 review | Opus / XHigh | job `823d9000`、session `823d9000-a8a2-4982-80a7-f1c89f8da371`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`，请求模型无偏差，1942698 ms 后 `REVISE`。实例键早于通知注销释放的有效阻断已在 `b858966` 修正并以顺序测试、30 轮实机交接和交接后 COM callback 复验；冷 marker 已实机确认，账户/UI 建议按冻结阶段 8 边界记录，其他 P2 已补测、修正或显式记录 | `$12.06`（CLI 状态显示值） |
| 45 | 2026-08-04 | 桌面 attention 与托盘生命周期 | 本机 Claude Code 2.1.220 后台 challenge/review | Opus / XHigh | job `c285b685`、session `c285b685-aeb4-4618-8fca-da8028e017a4`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`，请求模型无偏差，966545 ms 后返回定向修正。同步失败轮需共享 gate、Toast 默认音频导致多声、`FlashWindowEx` bool 是旧激活态及无窗口退出顺序发现均由 Codex 复算成立，并在 `1dbdf95` 修正后以 626 项回归、静音 payload、真实 HWND/托盘生命周期复验 | `$9.64`（CLI 状态显示值） |
| 46 | 2026-08-04 | 桌面 attention 与托盘生命周期 | 本机 Claude Code 2.1.220 后台固定检查点 review | Opus / XHigh | job `819c9403`、session `819c9403-02a9-4e71-9628-3f7f6d14c4fa`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`，请求模型无偏差，740546 ms 后 `PASS`、无 P0/P1。零句柄诊断/测试、独立 dispatch 与声音 false 测试、取消会话结束闭锁恢复及隐藏托盘/系统注销边界四项 P2 经 Codex 复算并在 `93e4740` 收敛，最终 Fast/Full 629 项通过 | `$3.89`（CLI 状态显示值） |
| 47 | 2026-08-04 | production 账户壳 | 本机 Claude Code 2.1.220 后台 challenge | Opus / XHigh | job `199dd547`、session `199dd547-ee29-4f9a-9032-438a68c056b6`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`，17 分 26 秒后返回。认证失效僵死、通知降级、UI replay 重入、Dispose 竞争、validation 快照、activity 噪声与 scope 格式化发现均由 Codex 复算并在账户壳主体收敛 | `$8.26`（CLI 状态显示值） |
| 48 | 2026-08-04 | Client SQLite 测试隔离 | 本机 Claude Code 2.1.220 后台可靠性 review | Opus / XHigh | job `4a674abb`、session `4a674abb-76c6-481d-9df4-948c24eeb7cc`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-opus-5`，11 分 24 秒后确认 11 个 SQLite 类与 collection 覆盖精确重合，当前方案适合交付；未来守卫/共享 teardown 仅记录为 P2 演进项 | `$2.67`（CLI 状态显示值） |
| 49 | 2026-08-04 | production 账户壳 | 本机 Claude Code 2.1.220 后台固定检查点 review | Opus / XHigh | job `87982d3f`、session `87982d3f-8403-40ce-a3b9-c293735ad51d`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-opus-5`，12 分 47 秒后无 P0，给出 detach `HttpClient`、冷通知注销线程、停机分类、无障碍与登出双失败条件。成立项由 Codex 在 `93d8fd5` 修正；既有 sqlite 修复获无保留意见 | `$4.44`（CLI 状态显示值） |
| 50 | 2026-08-04 | 账户壳 review-fix 与凭据 barrier | 本机 Claude Code 2.1.220 后台最终 review | Opus / XHigh | job `1aa031c1`、session `1aa031c1-7f38-4d11-95a4-69dc96389b54`，`workspace=E:\WorkSpace\RelayCove`；实际 `claude-opus-5`，16 分 31 秒后确认文件锁跨 store 与新登录解除 barrier 成立，目录权限残留需 DEC；指出 live region 事件与 `CredentialClearFailed` 文案吞没。Codex 已补事件、状态传播、测试和 `DEC-024/032`，最终 Fast/Full 658 项通过 | `$5.01`（CLI 状态显示值） |
| 51 | 2026-08-04 | 账户隔离会话列表 | 0.5 持久前置 challenge | Opus / XHigh | job `a478573d-fa39-415d-ab3e-50ffd0c7e60b`，`workspace=E:\WorkSpace\RelayCove`；649,211 ms 后终态实际 `claude-sonnet-5`、`model_mismatch=true`。同步事件必须非阻塞并先退订、迟到读取需当前 runtime/phase 原子门、托盘单一组合快照、精确预览 join、post-filter、busy/损坏行和 selection 不推进 activity 等成立项由 Codex 复算并在 `3f6bfff/a853580` 收敛 | `$2.9937885` |
| 52 | 2026-08-04 | 会话列表固定提交复核 | 0.5 持久 review | Opus / XHigh | job `c26a051c-e97e-4182-a465-e2d96e723d22`，`workspace=E:\WorkSpace\RelayCove`；804,441 ms 后终态实际 `claude-sonnet-5`、`model_mismatch=true`。指出 Ready 列表缺失的待选通知 ID 会压制用户选择，以及空态/标题 live region 缺属性；Codex 在 `ea83e7b` 加入失效回退、三条回归和 Polite live region，其他权威门/撤权/竞态结论与本地复算一致 | `$4.33353075` |
| 53 | 2026-08-04 | 会话列表 review-fix | 0.5 持久窄复审 | Opus / XHigh | job `959dba5e-1361-4938-8b5a-a0fa2d7fec4e`，`workspace=E:\WorkSpace\RelayCove`；1,034,424 ms 后终态实际 `claude-sonnet-5`、`model_mismatch=true`，对原子未读发布、损坏行不计总数/只读集合、跨账户待选清理和死锁/迟到发布返回 `PASS`。运行中读到的随后选择/live region 收紧也未提出新阻断；最终本机 Full 673 项与重复回归通过 | `$7.04985325` |
| 54 | 2026-08-04 | 有界消息列表 | 0.5 持久 challenge | Opus / XHigh | job `b0d5a420-dafa-4ed3-9d03-15bd2971df62`，`workspace=E:\WorkSpace\RelayCove`；673,773 ms 后终态实际 `claude-sonnet-5`、`model_mismatch=true`。History 首次新行未读、渲染边界不得污染预览、历史中段 activity 与分页校验发现经 Codex 复算成立并修正；索引建议因本任务明确禁止 schema 变化而不采纳 | `$2.8707420000000003` |
| 55 | 2026-08-04 | 消息列表固定提交复核 | 0.5 持久 review | Opus / XHigh | job `ea0628ef-5c14-47a7-9f1c-d4cf251d1d8b` 错误落在 MCP 包 workspace 而非仓库；主代理识别后主动取消，终态 `SIGTERM`、实际 `claude-opus-5`、`model_mismatch=false`，无答案、用量或费用元数据，不作为审查证据 | `unavailable` |
| 56 | 2026-08-04 | 消息列表固定提交复核 | 0.5 持久 review | Opus / XHigh | job `090a3bc5-29fc-4cf6-a349-3da279083645`，`workspace=E:\WorkSpace\RelayCove`，固定 `ReviewHead=51b6502`；1,666,134 ms 后 CLI code 1，终态实际 `claude-sonnet-5`，因订阅额度 403 无正式答案。失败前指出等价 `ItemsSource` 重发滚动丢失与未应用 revision 被滚动回执越过，Codex 独立复算后在 `46a59f6` 修正并回归；不得记为 Claude 终局结论 | `$11.044499750000004` |
| 57 | 2026-08-04 | Text 发送可靠性 review | MCP 只读 review | Opus / XHigh | 当前 MCP consultation 因 `ANTHROPIC_API_KEY`/其他认证源优先于 claude.ai 登录而失败；无 job、模型、workspace、费用或结论 | `unavailable` |
| 58 | 2026-08-04 | Text 发送可靠性 review | 本机后台只读 CLI | Opus / XHigh | background id `0cb84cf1`、session `0cb84cf1-15f5-4b2a-8fbb-3fd7ff176f40`，`workspace=E:\WorkSpace\RelayCove`；CLI 返回 idle/blocked 且未收到任务，主代理确认无审查执行后主动停止，不作为审查证据 | `unavailable` |
| 59 | 2026-08-04 | Text 发送可靠性 review | 本机只读 CLI | Opus / XHigh | `workspace=E:\WorkSpace\RelayCove`、`permission-mode=plan`；订阅额度 403，未返回模型、费用或结论 | `unavailable` |
| 60 | 2026-08-04 | Text 发送可靠性 review | 本机只读 CLI 回退 | Sonnet / XHigh | `workspace=E:\WorkSpace\RelayCove`、`permission-mode=plan`；订阅额度 403，未返回模型、费用或结论 | `unavailable` |
| 61 | 2026-08-04 | WindowActivated / Periodic Sync 可靠性 challenge | MCP 只读 challenge | Opus / XHigh | 当前 MCP consultation 因 `ANTHROPIC_API_KEY`/其他认证源优先于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 62 | 2026-08-04 | WindowActivated / Periodic Sync 固定提交 review | 本机后台只读 CLI | Opus / XHigh | background id `0f5dda0b`、session `0f5dda0b-e23e-480d-9fa1-014b1cbdb360`，`workspace=E:\WorkSpace\RelayCove`、`permission-mode=plan`、工具限于 Read/Glob/Grep；订阅额度 403，停在 idle/blocked 且未读取仓库，主代理确认后主动停止，无正式答案或费用元数据 | `unavailable` |
| 63 | 2026-08-04 | Reply 客户端闭环 challenge | MCP 只读 challenge | Opus / XHigh | 当前 MCP consultation 因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 64 | 2026-08-04 | Reply 客户端闭环当前差异复核 | MCP 只读 review | Opus / XHigh | 当前暴露接口仍为兼容 `consult_claude`；调用因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 65 | 2026-08-04 | 安全链接识别与显式打开 | MCP 只读安全 challenge | Opus / XHigh | 当前暴露接口仍为兼容 `consult_claude`；调用因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 66 | 2026-08-04 | 新消息分割线与 read-through 稳定性 | MCP 只读可靠性 challenge | Opus / XHigh | 当前暴露接口仍为兼容 `consult_claude`；调用因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 67 | 2026-08-04 | 会话作用域提及候选协议与授权 | MCP 只读协议/安全 challenge | Opus / XHigh | 当前暴露接口仍为兼容 `consult_claude`；RPC 等待后调用仍因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录而失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 68 | 2026-08-04 | 客户端提及组合与可靠发送 | MCP 只读可靠性 challenge | Opus / XHigh | 当前 Desktop 仍只暴露兼容 `consult_claude`，未暴露 0.5 持久 start/check/read；RPC 等待后因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 69 | 2026-08-04 | 服务端附件流式上传与存储 | MCP 只读架构/安全 challenge | Opus / XHigh | 当前 Desktop 仍只暴露兼容 `consult_claude`；RPC 等待后因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 70 | 2026-08-04 | 附件消息绑定、授权下载与未绑定 lease | MCP 只读架构/安全 challenge | Opus / XHigh | 当前 Desktop 仍只暴露兼容 `consult_claude`；RPC 等待后因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 71 | 2026-08-04 | 客户端附件元数据原子入库 | MCP 只读架构/安全/可靠性 challenge | Opus / XHigh | 当前 Desktop 仍只暴露兼容 `consult_claude`；调用启动前因 `ANTHROPIC_API_KEY`/其他认证源优于 claude.ai 登录失败；无 job、模型、workspace 回执、费用或结论 | `unavailable` |
| 72 | 2026-08-04 | 客户端附件元数据原子入库固定差异 | 本机 Claude Code 2.1.220 后台只读 review | Opus / XHigh | session `eeabe5b9-7c05-48d2-aa7e-b65fb65192ef`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`，约六分钟内读取 DEC/schema/merge/tests，但宿主自动续跑中断后无正式答案；两次恢复均在模型调用前 `ConnectionRefused`、零 token。无 verdict、findings 或可靠费用，不得冒充通过 | `unavailable` |
| 73 | 2026-08-04 | 客户端非幂等上传与 durable 附件发送边界 | MCP 只读协议/数据库/可靠性 challenge | Opus / XHigh | 当前 Desktop 仍只暴露兼容 `consult_claude`；CLI 启动阶段失败，未返回 job、实际模型、workspace、费用、findings 或 verdict，不得冒充通过 | `unavailable` |
| 74 | 2026-08-04 | WPF FileDrop 与剪贴板 PNG 内存/隐私边界 | 本机 Claude Code 2.1.221 后台只读 challenge | Opus / XHigh | background job `c3e1ce54`、session `c3e1ce54-4569-4199-a6b0-05230901d53b`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`、请求模型无偏差，约 16 分钟后返回。100 MiB retained、非脱离像素源、混合文本吞没、缺取消、WIC 包装分类与 buffer/命名问题经 Codex 复算，成立项全部修正；单一切片由共享 draft/context/pending 边界支撑 | `$5.46`（CLI 状态显示值） |
| 75 | 2026-08-04 | 附件可信下载、账户隔离 cache 与撤权/崩溃边界 | 本机 Claude Code 2.1.221 后台只读 challenge | Opus / XHigh | background job `a4e60acf`，`workspace=E:\WorkSpace\RelayCove`，工具限于 Read/Glob/Grep；实际 `claude-opus-5`、请求模型无偏差，约 14 分钟后返回。locked `.part` 阻断 purge、redirect/decompression、同进程恢复、publish 取消清理与 Content-Encoding 发现经 Codex 复算成立并修正；disabled actor、ETag fallback 与 hash 截断等建议按现有 server 401、强验证器任务边界和完整 SHA-256 证据裁定不采纳 | `$5.18`（CLI 状态显示值） |

- 调用计数：`75`（全部终态；#55/#58/#62 已主动停止，#56/#57/#59/#60/#61/#63/#64/#65/#66/#67/#68/#69/#70/#71/#73 失败，#72 中断且恢复失败）；用户已取消固定次数上限，Claude 只用于关键架构/安全/协议/数据库/可靠性内容，普通审查由 Codex reviewer 子代理承担，且不因第二意见停止本地验证。
- 已确认精确费用合计：`$80.07301150`；另有 #44–#50/#74–#75 本机 Claude Code 状态显示值合计 `$56.61`（界面两位小数，未伪造更高精度），按显示值总计约 `$136.68301150`。其余四十次未返回费用，保持 `unavailable`，不得推定为 `$0`。
- 每次调用必须记录 `workspace_root`、实际模型、`model_mismatch` 与 `cost_usd`；调用失败或模型偏差不得冒充目标模型审查，也不得替代 Codex 固定差异与真实测试。

## 阻塞与用户 Gate

- 当前阻塞：无。
- 当前所需用户 Gate：无。
- `未验证`：用户已提供仓库外的香港 Light-A2 协作应用主机配置汇总，保留到 M5 真实 VPS/双客户端 Gate；当前不读取，届时仅最小读取所需配置且不把地址、密钥或凭据提交到仓库或测试日志。
- 只有 `AGENTS.md`、`WORKFLOW.md` 和 v1 执行目标列出的重大产品、不可逆、安全、凭据、真实体验或发布事项才请求用户裁决；普通工程实现由当前任务自行收敛。

## 恢复与更新规则

会话中断后按以下顺序恢复，不要求用户复述：

1. 读取本文件的当前快照。
2. 读取 [`STATUS.md`](STATUS.md) 和 `ActiveTask`。
3. 核对 `git status --short --branch`、`agent/v1-integration`、当前任务分支和最近提交。
4. 从最后一个已验证检查点继续；未知或未运行项目保持 `未验证`。

每个任务开始时更新 `ActiveTask`、`TaskStatus`、`NextAction` 和当前分支事实；每个绿色完成提交后更新集成头、绿色提交、Claude 账本、阻塞和 Gate。只有证据实际满足时才能把状态改成 `v1_rc_ready` 或 `released`。
