# RelayCove 1.0.0-rc.24 详细执行计划

## 1. 文档状态

- **计划状态：** `执行中；S0–S4 已完成，S5 经 owner 决定跳过且保持未验证，S6/S7 仍受验证与授权 Gate 约束`
- **目标版本：** `1.0.0-rc.24`
- **发布类型：** 内部 Windows Client RC
- **计划基准：** `b8a7fb1fc7537b74a58673913d5d5a5292e7b5ce`
- **首个执行分支：** `agent/stage-19-rc24-stabilization`
- **首个任务记录：** [`tasks/2026-08-08-stage-19-rc24-notification-recovery.md`](tasks/2026-08-08-stage-19-rc24-notification-recovery.md)
- **产品与架构真源：** [`../../RelayCove_工程落地方案.md`](../../RelayCove_工程落地方案.md)
- **执行规则：** [`WORKFLOW.md`](WORKFLOW.md)
- **当前状态：** [`STATUS.md`](STATUS.md)
- **UI 约束：** [`../ui-design-guidelines.md`](../ui-design-guidelines.md)

本文用于后续模型直接接手执行，不替代单个任务记录。每次只允许实施一个可独立验收的纵向切片；本计划中的后续切片必须在前一切片完成证据记录后再启动。

## 2. 当前已验证基线

### 2.1 代码与功能

- `已验证`：`main` 当前头为 `b8a7fb1`，最新提交为“Improve image previews and conversation navigation”。
- `已验证`：stage-18 客户端三栏 UI、频道分组、无气泡消息流、底部输入区、成员抽屉和三档 WPF 内置快照已经完成。
- `已验证`：图片消息自动下载预览、私聊完整成员列表、频道分组折叠和 UI 设计约束已经完成。
- `已验证`：当前状态页记录 Fast、Full、Release 0 警告、0 错误，共 1,637 项测试通过。
- `已验证`：当前没有活动产品缺陷清单；rc.24 不得在没有实际失败或 owner 反馈时臆造功能。

### 2.2 rc.23 发布证据

- 版本：`1.0.0-rc.23`
- 提交：`70ed0eaaa16ae4f47ce4c8a45fe681515769dff5`
- 文件：`RelayCove.Client-1.0.0-rc.23-win-x64.zip`
- 长度：`165845207` bytes
- SHA-256：`3f4384424c2e662299d195aedecc3be5008b7c8967272ce904b5263174b39d89`
- `已验证`：内部更新清单、HTTPS 下载、Content-Length、SHA-256 ETag 和 Range `206` 均通过。

### 2.3 未验证边界

- `未验证`：本计划制定时没有重新运行 Fast、Full 或 Release；上述结果来自当前状态页和已完成任务记录。
- `已验证`：rc.24 已从提交 `9730a14ea736c83355ec7d8af0a78c5e024c8562` 双构建；两份 ZIP 字节一致。
- `未验证`：尚未执行 rc.23→rc.24 更新演练。
- `未验证`：尚未执行本轮真实 Windows 通知、托盘、断网恢复和双账号人工矩阵。
- `未验证`：严格第二台 Windows 设备矩阵仍是可选发布增强，不是内部 rc.24 默认阻塞项。

## 3. rc.24 目标与成功定义

rc.24 的目标不是扩展产品边界，而是证明 rc.23 之后的客户端在通知、恢复、图片预览、导航和更新链路上仍然可靠，并只修复由真实证据确认的问题。

完成后必须达到以下可观察结果：

1. Fast 基线和指定定向回归全部通过。
2. 若发现失败，必须先稳定复现，再增加回归测试并实施最小修复。
3. Full 与 Release 0 警告、0 错误，全部自动化测试通过。
4. 构建两个字节一致的 rc.24 Windows x64 自包含 ZIP，并验证包清单、内容边界和 SHA-256。
5. 使用精确 rc.23 与 rc.24 包完成更新交付 smoke；更新失败不破坏旧可运行版本。
6. 完成至少一台真实 Windows 机器、两个账户的通知/托盘/断网/附件/导航/更新人工矩阵。
7. 所有未执行场景明确标记为 `未验证`，不得用自动化替代真实 Windows 结果。
8. 推送、合并、VPS 写入和内部更新发布仅在 owner 明确授权后执行。

## 4. 默认范围与非目标

### 4.1 默认范围

- Windows Client 可用性和可靠性回归。
- 通知策略、通知去重、托盘提醒和激活路由。
- SignalR 断开/重连、Sync 补拉和 Realtime/Sync 候选协调。
- 图片自动预览、附件下载/重试和安全解码。
- 会话导航、滚动、分组折叠、成员抽屉和小窗口布局。
- Client/Updater 发布包、更新清单与 rc.23→rc.24 更新演练。
- 与上述行为直接相关的客户端测试和任务证据。

### 4.2 默认不做

- 不增加 Web 聊天端、移动端、组织架构、复杂权限、在线状态或新消息类型。
- 不引入新的 UI 框架、消息队列、Redis、搜索引擎、对象存储或大型依赖。
- 不修改 Shared DTO、Server API、SignalR 事件、数据库或消息可靠性语义来解决纯客户端问题。
- 不实现逐成员频道角色。若 owner 明确要求，必须单独建立协议切片、决策记录和独立审查。
- 不把 public Tag/GitHub Release、代码签名、安装器、SmartScreen 信誉或第二台 Windows 严格矩阵默认并入内部 rc.24。
- 不自动推送、合并、部署或替换线上更新清单。

### 4.3 版本边界

- 默认只发布 Client `1.0.0-rc.24`，Server 保持现网版本不变。
- 若问题必须修改 Server、Shared、迁移或公共协议，立即停止当前切片，另建任务和分支，并重新确定是否需要 Server rc.24 配套发布。
- 默认生成 optional 更新；是否提高 `MinimumSupportedVersion` 或设置 `Mandatory` 必须由 owner 单独确认，不能从版本号推断。

## 5. 执行总览

| 顺序 | 切片 | 主要输出 | 进入条件 | 完成门槛 |
| --- | --- | --- | --- | --- |
| S0 | 启动与绿色基线 | 任务记录、Fast 证据、测试覆盖清单 | 工作区干净 | Fast 通过或记录可重复基线失败并停止 |
| S1 | 通知、重连与恢复门禁 | 定向测试结果、覆盖矩阵、必要的最小修复 | S0 通过 | 指定回归通过，无未解释失败 |
| S2 | UI/图片/导航反馈切片 | 一个真实反馈对应的修复和回归 | 有 owner 反馈或稳定复现 | 单一行为验收通过，不扩大协议 |
| S3 | 全量质量门禁 | Full、Release、格式、差异检查证据 | 所有修复切片完成 | 0 警告/0 错误、全部测试通过 |
| S4 | 可复现客户端包 | 两份字节一致 ZIP、manifest、hash | 干净已提交 HEAD | 双构建一致，离线 verifier 通过 |
| S5 | 更新交付演练 | rc.23→rc.24 smoke 证据 | 精确旧包和新包可用 | 下载、校验、替换、启动与恢复通过 |
| S6 | Windows 人工验收 | 场景矩阵和截图/日志摘要 | 可运行 rc.24 包、两账户 | 必需场景通过，无 P0/P1 |
| S7 | 内部发布交接 | 发布说明、限制、授权请求 | S3–S6 通过 | owner 明确授权后才推送/部署 |

## 6. S0：启动与绿色基线

### 6.1 启动检查

后续模型接手后首先执行：

```powershell
git status -sb
git branch --show-current
git log -1 --format="%H%n%ad%n%s" --date=iso-strict
git diff --check
```

预期：

- 分支为 `agent/stage-19-rc24-stabilization`。
- 除本计划、stage-19 任务记录和经计划允许的状态文档外，没有未知改动。
- 若出现其他修改，不得覆盖、暂存、提交或清理，立即停止并向 owner 报告。

### 6.2 Fast 基线

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
```

必须记录：

- restore、Debug build、各测试项目数量和总数。
- 退出码、警告数、错误数。
- 失败测试的完整限定名，但不得把 token、用户名、服务器私密配置或消息正文写入任务记录。

失败处理：

1. 原样单独复跑失败测试一次。
2. 再复跑所属测试项目一次。
3. 只有能够稳定复现才进入修复；偶发失败记录为抖动证据，不得通过放宽断言或增加无界等待掩盖。
4. 若失败来自基线且不属于当前任务范围，按工作流停止并询问。

## 7. S1：通知、重连与恢复门禁

### 7.1 定向自动化

Fast 成功后运行 Release 定向测试：

```powershell
dotnet test ./tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj `
  --configuration Release `
  --no-restore `
  --filter "FullyQualifiedName~Notifications|FullyQualifiedName~Realtime|FullyQualifiedName~Sync|FullyQualifiedName~Desktop|FullyQualifiedName~Updates" `
  --logger "console;verbosity=minimal"
```

如 Release 尚未构建，先执行：

```powershell
dotnet build ./RelayCove.sln --configuration Release --no-restore
```

必须重点核对以下现有测试区域：

- `Notifications/ClientNotificationCoordinatorTests.cs`
- `Notifications/ClientNotificationRoundCoordinatorTests.cs`
- `Notifications/WindowsClientNotificationPlatformTests.cs`
- `Notifications/WindowsNotificationActivationCodecTests.cs`
- `Storage/NotificationRecoveryTests.cs`
- `Storage/NotificationStateAdoptionTests.cs`
- `Realtime/ClientRealtimeConnectionTests.cs`
- `Sync/ClientSyncCoordinatorTests.cs`
- `Accounts/ClientAutomaticSyncSchedulerTests.cs`
- `Desktop/ClientTrayHostTests.cs`
- `Desktop/WindowsDesktopNotificationAttentionTests.cs`
- `Updates/ClientUpdateCoreTests.cs`
- `Desktop/ClientUpdateHandoffTests.cs`

### 7.2 规范覆盖矩阵

执行者必须在 stage-19 任务记录中逐项标记：

| 场景 | 自动化要求 | 人工要求 |
| --- | --- | --- |
| Startup 补拉 | 候选汇总、游标提交和通知策略 | 启动后显示未读 |
| WindowActivated | 补历史但不弹旧通知 | 前台切回不自扰 |
| Reconnect/Periodic | 前后台策略、single-flight、失败恢复 | 断网恢复后补拉 |
| 阈值 10/11 | 10 条逐条、11 条汇总或当前冻结策略 | 仅在需要视觉确认时抽测 |
| Toast 临时失败 | 候选保持、后续恢复 | 系统通知不可用时观察回退 |
| Sync 失败 + Realtime | Realtime 候选解闸且只决策一次 | 双账号断网竞态抽测 |
| 托盘/闪烁 | attention 至多一次、异常不影响 Toast | 最小化和关闭到托盘实测 |
| 旧账户/撤权 Toast | 路由 fail-closed、不能打开缓存 | 点击陈旧通知实测 |
| SignalR 重连 | 状态顺序、重新认证、当前权限分组 | 网络切换观察状态恢复 |

### 7.3 修复决策树

- 全部通过且覆盖满足规范：不修改产品代码，完成门禁记录。
- 自动化缺少一个可确定验证的边界：只新增最小测试；若测试暴露缺陷，再修改最小产品代码。
- 失败涉及 Shared/Server/数据库/授权语义：停止并拆分独立任务。
- 失败只在真实 Windows 环境可见：记录最小复现步骤，进入 S6，不伪造自动化结论。

## 8. S2：UI、图片与导航反馈切片

本切片不是自动开始项。只有满足以下任一条件才进入：

1. owner 提供明确的 rc.23 使用反馈；
2. S0/S1 或人工矩阵发现稳定可复现问题；
3. 现有快照/展示测试证明布局或交互回归。

每个反馈必须单独记录：

- 环境、窗口尺寸、DPI、账户状态和会话类型。
- 最小复现步骤、预期结果、实际结果。
- 严重级别和影响范围。
- 修复允许修改的文件。
- 回归测试名称 `Method_WhenCondition_ExpectedResult`。

优先检查但不得无证据修改的区域：

- 图片首次可见时自动下载、下载失败重试和安全缩略图解码。
- 会话切换后的选择、定位、搜索跳转和滚动位置。
- 分组折叠状态与未读徽标不丢失。
- 1280×720 下输入区、附件预览、提及候选和发送按钮不裁切。
- 1400 像素阈值附近成员抽屉自动收起和重新打开。
- 私聊成员列表不出现频道管理入口。

定向入口：

```powershell
dotnet test ./tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj `
  --configuration Release `
  --no-restore `
  --filter "FullyQualifiedName~ClientUiSnapshotTests|FullyQualifiedName~MainWindowNavigationPresentationTests|FullyQualifiedName~MainWindowAttachmentImagePresentationTests|FullyQualifiedName~ClientConversationListPresenterTests|FullyQualifiedName~ClientMessageListPresenterTests" `
  --logger "console;verbosity=minimal"
```

## 9. 缺陷分级与进入版本规则

| 等级 | 定义 | rc.24 处理规则 |
| --- | --- | --- |
| P0 | 数据丢失、越权、秘密泄露、更新破坏可运行版本 | 立即停止普通发布；修复、独立安全复核和完整回归后才能继续 |
| P1 | 漏消息、重复消息/通知、无法登录/更新、稳定崩溃 | 必须修复，增加回归，Full 后独立复核 |
| P2 | 常用操作受阻但有安全绕行，如导航、预览、布局严重问题 | 有稳定复现则纳入；一次只修一个切片 |
| P3 | 文案、轻微视觉或低频体验 | 默认延后，除非修复机械且不会扩大验证范围 |

没有复现证据的问题不得因为“可能发生”进入 rc.24。

## 10. S3：全量质量门禁

所有代码修复完成后运行：

```powershell
pwsh ./scripts/verify.ps1 -Mode Full
```

该脚本实际执行：

1. `dotnet restore`
2. `dotnet format --verify-no-changes`
3. Release build
4. Release 全部测试
5. `git diff --check`

通过标准：

- 所有项目 0 警告、0 错误。
- 所有测试通过，数量记录到任务文件和 `STATUS.md`。
- 格式和空白检查通过。
- 不允许用跳过测试、放宽断言、删除测试或改变超时掩盖失败。

提交前自审：

```powershell
git status -sb
git diff --stat
git diff --check
git diff
```

重点检查：

- 日志脱敏、取消、异常和 UI 线程。
- 通知去重、账户 scope、撤权和旧激活路由。
- Realtime/Sync 并发及消息幂等。
- 未请求的重构、依赖、调试代码和生成物。

若修改了通知、同步、更新或部署逻辑，按仓库规则必须在本地提交后进行独立复核。不得让审查者直接修改文件。

## 11. 分支与提交策略

### 11.1 首个切片

- 分支：`agent/stage-19-rc24-stabilization`
- 任务：通知、重连与恢复门禁。
- 允许在验证通过后创建本地提交。
- 建议提交信息：`Verify rc24 notification recovery gate`；若有修复，应使用描述行为的祈使句，例如 `Fix notification recovery after reconnect`。

### 11.2 后续反馈切片

- 每个独立反馈从最新绿色 `main` 或 owner 指定集成头创建单独 `agent/stage-19-<slug>` 分支。
- 不在一个提交混入多个不相关反馈。
- 不在验证失败时合并多个候选修复。

### 11.3 外部写入

- 本地提交：Full 与所需复核通过后允许。
- 推送、创建 PR、合并、Tag、Release、VPS 上传和线上 manifest 替换：必须再次取得 owner 明确授权。

## 12. S4：可复现 rc.24 客户端包

发布包必须来自干净、已提交、已验证的精确 HEAD。发布脚本默认拒绝脏工作区，正式包不得使用 `-AllowDirty`。

### 12.1 记录精确 HEAD

```powershell
$releaseCommit = git rev-parse HEAD
git status --porcelain
```

第二条命令必须无输出。

### 12.2 双构建

```powershell
pwsh ./scripts/publish-client.ps1 `
  -Version 1.0.0-rc.24 `
  -OutputRoot ./artifacts/rc24-build-a

pwsh ./scripts/publish-client.ps1 `
  -Version 1.0.0-rc.24 `
  -OutputRoot ./artifacts/rc24-build-b
```

### 12.3 离线验证与字节一致性

```powershell
pwsh ./scripts/verify-client-release.ps1 `
  -Version 1.0.0-rc.24 `
  -OutputRoot ./artifacts/rc24-build-a `
  -CompareOutputRoot ./artifacts/rc24-build-b `
  -ExpectedCommit $releaseCommit
```

必须记录：

- 精确提交、SDK、包长度和 SHA-256。
- 两份 ZIP 字节一致。
- 包内 manifest、文件 hash、PE x64、自包含运行时和秘密排除检查通过。
- 发布目录不包含源码、PDB、数据库、日志、缓存、凭据、密钥或临时文件。

### 12.4 本轮证据（2026-08-08）

- `已验证`：精确提交 `9730a14ea736c83355ec7d8af0a78c5e024c8562`，工作区在两次构建前均干净。
- `已验证`：SDK `10.0.101`，版本 `1.0.0-rc.24`，两份 `RelayCove.Client-1.0.0-rc.24-win-x64.zip` 字节一致。
- `已验证`：ZIP 长度 `165908074` bytes，SHA-256 `057a4683921166e03001d3d4bd0eb1bc2b9591fd84fb59fbcf6c19cbe223c228`。
- `已验证`：离线 verifier 已核验 manifest、文件 hash、PE x64、自包含运行时、秘密排除和重复 ZIP 比较。

## 13. 更新清单决策与生成

### 13.1 发布策略 Gate

生成清单前由 owner 明确以下值：

- `DownloadUrl`：批准的 HTTPS artifact URL。
- `MinimumSupportedVersion`：默认继承当前线上批准值，不凭记忆填写。
- `Mandatory`：默认 `false`；只有存在安全/兼容阻断且 owner 明确决定时才设为 true。
- `ReleaseNotes`：只写用户可观察变化、修复和已知限制。

### 13.2 生成候选清单

以下命令中的占位值必须先替换：

```powershell
pwsh ./scripts/generate-update-manifest.ps1 `
  -Version 1.0.0-rc.24 `
  -MinimumSupportedVersion <approved-minimum-version> `
  -DownloadUrl <approved-https-download-url> `
  -ReleaseNotes <approved-release-notes> `
  -ClientReleaseRoot ./artifacts/rc24-build-a `
  -OutputRoot ./artifacts/rc24-manifest `
  -ExpectedCommit $releaseCommit
```

只有 owner 明确批准强制更新时添加 `-Mandatory`。

清单生成不等于发布；正式更新目录必须先原子发布 ZIP，最后替换 `manifest.json`。

## 14. S5：rc.23→rc.24 更新交付演练

### 14.1 输入要求

- 精确 rc.23 ZIP，长度和 SHA-256 必须匹配第 2.2 节。
- 通过 S4 验证的精确 rc.24 ZIP。
- 不从未知缓存、聊天附件或未验证下载目录取包。

本轮状态：`未验证`。已在本机工作区和 `D:\WorkSpace` 查找精确 rc.23 ZIP，未找到与第 2.2 节长度和 SHA-256 匹配的归档；又在隔离 worktree 从提交 `70ed0eaaa16ae4f47ce4c8a45fe681515769dff5` 使用 SDK `10.0.101` 重建。重建结果为 `165907053` bytes、SHA-256 `854838e9e1de2c8deb6335da673ecb3a91f438c9632ec726621fa181240c5b64`，不匹配第 2.2 节的历史发布身份，已拒绝用于 smoke。因此未执行 smoke，也未使用未知缓存或重新下载的未验证包。

### 14.3 Owner 决定（2026-08-08）

- owner 明确决定跳过 rc.23→rc.24 更新交付演练；该场景保持 `未验证`，不作为通过结果或发布质量证明。
- 此决定不授权生成线上清单、推送、合并、部署或写入内部更新通道。

### 14.2 自动更新 smoke

```powershell
pwsh ./scripts/verify-update-delivery.ps1 `
  -OldVersion 1.0.0-rc.23 `
  -NewVersion 1.0.0-rc.24 `
  -OldArchivePath <verified-rc23-zip> `
  -NewArchivePath <verified-rc24-zip>
```

该 smoke 必须证明：

- 真实 Server 托管精确清单和 ZIP。
- HTTPS/Client-equivalent 下载产生受控 `.part` 文件。
- 长度与 SHA-256 不匹配时拒绝发布下载。
- 外部 Updater 等待旧进程退出、执行同卷替换并启动新版本探针。
- 失败路径收敛到完整旧包或完整新包，不留下混合目录。
- `%LOCALAPPDATA%\RelayCove` 账户数据不属于便携包替换范围。

测试失败时保留证据目录；只有需要诊断 Server 日志时使用 `-KeepServerLog`，日志仍不得包含秘密。

## 15. S6：真实 Windows 人工验收矩阵

### 15.1 环境记录

每次人工验收必须记录：

- Windows 版本和补丁号。
- 机器标识使用非敏感别名，例如 `Win-A`、`Win-B`。
- 客户端版本、提交、ZIP SHA-256 和启动目录。
- 显示分辨率、缩放比例、通知设置和免打扰状态。
- 服务器地址只记录批准的环境别名，不在任务记录写 token、密码或私密 URL 参数。
- 账户使用 `Account-A`、`Account-B` 代称。

### 15.2 必需场景

| ID | 场景 | 操作 | 预期 |
| --- | --- | --- | --- |
| W01 | 前台实时私聊 | A 给前台 B 发消息 | B 立即显示一次，无重复通知 |
| W02 | 最小化通知 | 最小化 B，A 发消息 | B 显示通知，任务栏 attention 至多一次 |
| W03 | 托盘通知 | 关闭 B 主窗口进入托盘，A 发消息 | B 仍收到提醒，点击可回到正确会话 |
| W04 | 断网补拉 | B 断网，A 连发 3 条，B 恢复网络 | 自动补拉 3 条，不重复显示，通知策略唯一 |
| W05 | 彻底退出恢复 | B 彻底退出，A 发消息，B 再启动 | 启动后补拉并显示未读，不要求退出期间实时通知 |
| W06 | 旧账户 Toast | B 切换账户后点击旧通知 | 不打开当前账户同 ID 内容 |
| W07 | 撤权 Toast | 私有频道撤权后点击陈旧通知 | 不显示已撤权缓存 |
| W08 | 单实例 | 客户端运行时再次启动 | 不出现第二个主实例，激活转交成功 |
| W09 | 图片自动预览 | A 发 PNG/JPEG，B 打开会话 | 自动下载并显示安全缩略图，失败可重试 |
| W10 | 普通附件 | 发送文件并下载/取消/重试/打开/定位 | 进度和状态正确，无权账户无法下载 |
| W11 | 导航与滚动 | History/Around/搜索跳转、新消息到达 | 查看历史时不被强制到底，定位准确 |
| W12 | 小窗口 | 1280×720，展开回复/@/10 附件预览 | 输入框、附件/@和发送按钮可用，不裁切 |
| W13 | 成员栏 | 1600×900 展开成员栏；缩窄到 <1400 | 操作可见；窄化自动收起且不挤压聊天 |
| W14 | optional 更新 | rc.23 检查 rc.24、下载、稍后/安装 | 清单、进度、hash 和交接正确；可稍后处理 |
| W15 | 更新失败保护 | 使用损坏副本或受控失败场景 | 拒绝安装，rc.23 仍可运行 |

### 15.3 窗口与视觉矩阵

- 自动化内置渲染：1280×720、1600×900、1920×1080 全部必须通过。
- 真实桌面至少抽测 1280×720 或等效可用区域，以及当前日常分辨率。
- 严格第二台 Windows 机器、不同 DPI/显卡/通知环境为公开发布前推荐项；内部 rc.24 默认记录为可选，除非 owner 提升为必需。

### 15.4 结果记录格式

```text
状态：已验证 | 未验证 | 失败
环境：Win-A / Windows <version> / scale <percent>
包：1.0.0-rc.24 / <commit> / <sha256>
场景：Wxx
步骤：...
预期：...
实际：...
证据：截图路径、非敏感日志路径或测试输出摘要
限制：...
```

## 16. S7：内部发布与线上更新切换

本节只描述获得授权后的操作边界，不构成当前授权。

### 16.1 发布前 Gate

- Full、Release、双构建、离线 verifier、更新 smoke 和必需 Windows 矩阵全部通过。
- 工作区干净，HEAD 和 rc.24 manifest 内 commit 一致。
- 无 P0/P1；接受的 P2/P3 必须写入 release notes。
- owner 明确批准推送、合并和内部更新发布。

### 16.2 远端发布顺序

1. 推送任务分支。
2. 按仓库要求完成独立复核。
3. 仅快进合并到 owner 指定分支或 `main`。
4. 从合并后的精确绿色提交重新构建正式包；不得复用合并前不同提交的 ZIP。
5. 再次运行离线 verifier 和必要 update smoke。
6. 在 VPS 暂存目录上传 ZIP 和 manifest 候选。
7. 校验 ZIP 精确 SHA-256。
8. 先原子切换 ZIP，最后原子替换 `manifest.json`。
9. 验证公网 manifest、GET Content-Length、SHA-256 ETag 和 Range `206`。
10. 用 rc.23 客户端执行一次真实 optional 更新。

### 16.3 线上回退准备

- 保留 rc.23 ZIP、旧 manifest、长度、SHA-256 和权限信息。
- rc.24 验收完成前不删除旧 artifact。
- 若新 manifest 或下载验证失败，原子恢复旧 manifest；不得发布指向不存在或未完成 ZIP 的清单。
- Updater 只保证目录替换恢复到完整旧包或新包，不承诺新客户端启动后的自动健康回滚。

## 17. 风险与控制

| 风险 | 控制措施 |
| --- | --- |
| 没有真实反馈却扩大功能 | 只有 owner 反馈、稳定失败或覆盖缺口才能进入修复 |
| 通知/同步修复引入重复副作用 | 保持 INSERT-first、single-flight、round gate、账户 scope 和撤权 fail-closed；增加并发回归 |
| WPF 测试抖动被误判 | 单测复跑、项目复跑、记录频率；禁止无界等待和简单放宽断言 |
| 发布包来自脏树 | 正式 publish 不使用 `-AllowDirty`，manifest commit 必须等于 HEAD |
| 两次构建内容不同 | 使用 `CompareOutputRoot` 做字节一致性验证 |
| manifest 先于 ZIP 上线 | ZIP 校验并原子切换后最后替换 manifest |
| 更新失败破坏用户数据 | 包替换不触碰 `%LOCALAPPDATA%\RelayCove`；执行旧/新完整目录恢复 smoke |
| 旧 Toast 越权打开内容 | 账户、会话授权和撤权路由必须 fail-closed，并做人工点击验证 |
| 秘密进入日志或证据 | 只记录类型、状态、非敏感环境别名和 hash；不复制 token、密码、消息正文 |
| Server/协议边界被顺手修改 | 一旦需要 Shared/Server/迁移，停止并拆分独立任务与审查 |

## 18. 强制停止条件

出现以下任一情况立即停止，保留现场并请求 owner 决定：

- 工作区出现未知或不相关修改。
- Fast/Full 基线失败且无法证明属于当前切片。
- 发现秘密、生产数据、不可逆写入或路径不明确的删除操作。
- 修复需要公共协议、数据库兼容策略、认证/授权变化或大型依赖。
- 更新演练需要真实内部服务器写入、凭据、第二账户或系统设置变更而未获授权。
- 接受标准出现会显著改变结果的多种解释。
- 无法运行必要验证，且没有足够证据证明修改安全。

## 19. 文档与证据更新规则

每个切片完成时必须更新对应任务记录：

1. 修改摘要。
2. 精确命令、退出码、测试数量和构建结果。
3. 新增、修改、删除文件。
4. 已验证、未验证和假设。
5. 已知限制。
6. 单一明确的下一步。

只有状态真实变化时更新 `STATUS.md`。不要把完整终端输出、思考过程、token、服务器秘密或用户消息正文复制到仓库。

建议证据目录仅用于本地生成物，不提交大文件：

```text
artifacts/rc24/
  automated/
  packages/
  update-smoke/
  manual/
```

实际发布脚本的输出路径仍以脚本参数和已验证目录为准。

## 20. 后续模型接手指令

后续模型切换后按以下顺序执行：

1. 完整阅读 `AGENTS.md`。
2. 阅读工程方案第 6、12、13、20、21 章。
3. 阅读 `docs/ai/STATUS.md`、本计划和 stage-19 任务记录。
4. 执行 `git status -sb`，确认分支和允许的文档改动。
5. 不删除或覆盖本轮计划文件。
6. 先运行 Fast；不得先改产品代码。
7. 运行 S1 定向测试并填写覆盖矩阵。
8. 只有稳定失败或明确反馈才实施修复。
9. 每个修复先补回归测试，完成后运行定向测试和 Full。
10. 在推送、合并、VPS 或更新通道写入前停止并请求 owner 明确授权。

建议接手提示词：

```text
继续 RelayCove rc.24 Goal。完整阅读 AGENTS.md、RelayCove_工程落地方案.md 的相关章节、docs/ai/STATUS.md、docs/ai/RC24_EXECUTION_PLAN.md 和 docs/ai/tasks/2026-08-08-stage-19-rc24-notification-recovery.md。当前只执行 S0/S1：检查工作区、运行 Fast 和通知/重连/恢复/更新定向回归，填写覆盖矩阵。没有稳定失败或明确反馈时不要修改产品代码。触发停止条件时保留现场并询问。不得推送、合并、部署或写线上更新通道。
```

## 21. 最终完成清单

### 自动化

- [x] Fast 通过。
- [x] 通知/Realtime/Sync/Desktop/Updates 定向回归通过。
- [x] UI/图片/导航相关定向回归通过。
- [x] Full 通过。
- [x] Release 0 警告、0 错误。
- [x] `git diff --check` 通过。

### 包与更新

- [x] rc.24 两次干净构建字节一致。
- [x] 离线 Client release verifier 通过。
- [x] 记录 ZIP 长度、SHA-256、commit 和 SDK。
- [ ] rc.23→rc.24 update delivery smoke 通过（owner 已决定跳过；保持未验证）。
- [ ] optional/mandatory/minimum version 经 owner 明确决定。

### Windows 人工验收

- [ ] 双账号前台实时消息。
- [ ] 最小化通知与任务栏 attention。
- [ ] 托盘通知与点击跳转。
- [ ] 断网三条消息补拉，无重复。
- [ ] 退出后启动恢复。
- [ ] 旧账户和撤权 Toast fail-closed。
- [ ] 图片自动预览和附件下载/重试/打开。
- [ ] 导航、滚动、搜索跳转和小窗口布局。
- [ ] rc.23→rc.24 真实更新及失败保护。

### 发布交接

- [ ] 无 P0/P1。
- [ ] 已接受限制写入 release notes。
- [ ] 状态页和任务记录更新。
- [ ] 独立复核完成。
- [ ] owner 明确授权推送/合并/内部发布。
- [ ] 正式 artifact 与 manifest 从合并后的精确提交重新生成并验证。

## 22. 当前下一步

后续模型只执行 S0 和 S1：运行 Fast、通知/重连/恢复/更新定向回归，并完成 stage-19 覆盖矩阵。当前不得直接进入产品代码修改或线上发布。
