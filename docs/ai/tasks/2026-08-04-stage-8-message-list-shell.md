# 阶段 8 有界消息列表、History/Around 与渲染后读穿

## 任务定义

- **任务名称：** 阶段 8 当前会话消息列表、本地首屏、History/Around 与渲染后读穿
- **状态：** `进行中`
- **基准提交：** `a477d68d8477b6d3bee938b29ff22a3cfcb7fa05`
- **工作分支：** `agent/stage-8-message-list-shell`
- **相关方案章节：** 9.2–9.4、10.4、12.3、12.6–12.8、阶段 8；`DEC-017`、`DEC-020`、`DEC-026`、`DEC-027`、`DEC-030`、`DEC-033`

### 目标

把当前已授权会话接入真实、虚拟化且有界的消息视图：普通选择先显示本地最新窗口并可向上懒加载 History，消息通知在本地缺失目标时经 Around 定位。只有当前账户/当前选择的消息快照真正应用到 WPF 后，才把该会话登记为打开并推进本地 read-through；切换、注销和撤权必须 fail-closed。

### 已知事实

- `已验证`：基准 `a477d68` 已仅快进并推送到 `agent/v1-integration`；新分支建立时工作树干净，Fast 通过 Shared 35、Server 175、Client 462、Updater 1，共 673/673，Debug 构建 0 警告、0 错误。
- `已验证`：Shared/Server 已实现并测试 History 的排除式 `beforeMessageId` keyset、升序响应和 Around 双侧窗口；稳定撤权 `403 ConversationAccessRevoked` 是可触发本地清理的权威信号。
- `已验证`：客户端 cache 已有账户/权威快照/deny-set 门、统一 Realtime/Sync/History/SendResponse 合并、read-through pending/安全上传及提交后状态信号；现有 `ReadMessagesAsync` 会读取全部已缓存服务端消息，不适合作为 production 虚拟化首屏接口。
- `已验证`：账户 coordinator 已拥有当前 runtime 的订阅、版本化发布和旧 runtime 隔离；WPF 会话选择当前只改变高亮，`OpenConversationId` 始终为 `null`，因此尚未把未渲染消息误判为已读。
- `已验证`：通知激活目标同时携带账户 scope、会话 ID 和消息 ID，现有授权路由只会把当前 scope 中可访问的目标交给 UI。

### 假设

- `假设`：首屏和每次 History 页固定为最多 50 条；本地读取与网络页都保持服务端消息 ID 严格升序，较旧页只前插，不把正在查看历史的用户强制滚到底。
- `假设`：普通选择可在本地首屏应用后异步补取最新 History；通知目标优先使用本地消息，缺失时调用 Around。Around 是定位入口，不替代向上 History 的连续分页状态。
- `假设`：本切片只显示服务端已确认的 Text/System 等现有 `MessageDto`；pending 发送行留到发送切片统一接入，不能伪造成已发送消息。

### 范围

- 必须实现：
  - 在 cache gate 与当前权威/撤权门内提供有界、不可变、稳定排序的本地消息窗口；逐行/页面损坏、busy、fatal 和撤权有明确失败状态，不把全库消息直接交给 UI。
  - 增加账户作用域 History/Around HTTP 读取与严格响应校验；每个远端页面通过现有唯一合并语义原子写入，History 不更新会话预览、不推进 Sync cursor、不产生未读/Toast/声音。
  - 对同一当前选择实行取消、generation 和 single-flight/dirty 合流；旧账户、旧会话、迟到 HTTP/缓存读取、撤权和注销不得发布消息或恢复 activity。
  - 发布版本化、不可变的消息视图快照，支持本地首屏、向上加载更多、通知目标定位和明确的 loading/empty/transient/fatal/revoked 状态。
  - WPF 在 Dispatcher 上应用虚拟化消息集合，保留历史阅读位置；快照实际应用后回执 coordinator，只有仍为当前账户/选择/版本时才设置 `OpenConversationId`、单调标记已渲染边界并触发既有 read-through 上传。
- 允许修改：
  - `src/RelayCove.Client/Storage/`、`Sync/`、`Accounts/`、`App.xaml.cs`、`MainWindow.xaml(.cs)`。
  - 对应 `tests/RelayCove.Client.Tests/`，以及本任务必要的 `docs/ai/` 记录。
- 明确不做：
  - 消息发送、输入框、失败重试、回复、@、链接/复制、日期/新消息分割线、搜索、附件、头像下载和周期 Sync 调度。
  - Shared/Server 协议、SQLite schema/migration、DPAPI/通知激活编码或新依赖。

### 验收标准

- [ ] 本地首屏和 older window 均最多 50 条、严格升序、去重且真正只读；大量历史不会被一次性读入 UI，未权威/fatal/撤权/损坏状态不泄露旧数据。
- [ ] History/Around 对 URI、页边界、会话归属、目标归属、顺序、数量、continuation/has-more 组合做防御校验；页面任一冲突或错误不留下部分合并，稳定撤权 `403` 收敛到 tombstone/通知清理。
- [ ] 快速切换会话、账户切换、注销、撤权、重入加载和迟到结果不能覆盖当前视图；同一方向重复加载合流，不死锁 cache/runtime/UI。
- [ ] 消息列表启用 WPF recycling virtualization；旧页前插保持用户位置，新 Realtime 只有用户位于最新区域时跟随到底，否则保留位置并显示新消息提示。
- [ ] 只有当前版本消息快照已由 Dispatcher 应用后才设置 activity 和推进 read-through；仅选择、加载中、失败、空/失效目标和旧版本回执均不标记已读。
- [ ] Fast/Full、消息 cache/History/Around/coordinator/WPF 定向与竞态重复、model drift、八项目漏洞审计、空白检查和真实 Windows 进程 smoke 通过；无真实账户/VPS/第二客户端的场景如实保留未验证。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~MessageList|FullyQualifiedName~MessageHistory|FullyQualifiedName~MessageAround|FullyQualifiedName~ClientAccountShell"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须改变 Shared/Server 协议、SQLite schema/migration、账户隔离/权威快照/撤权语义或通知激活编码。
- 需要把“选中”本身等同于已读、允许旧选择后台结果改变 activity，或引入新依赖才能满足验收。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4、10.4、12.3、12.6–12.8、阶段 8、docs/ai/STATUS.md 和本任务文件。
只实现有界消息视图、History/Around 懒加载和渲染后 read-through，不实现发送、输入、搜索或附件。
先确认 cache 的原子合并/撤权门、runtime 所有权和 Dispatcher 回执，再写最小读取、网络与发布路径。
所有旧账户/旧选择迟到结果、未完成权威快照和撤权路径必须 fail-closed；UI 集合只在 Dispatcher 更新。
完成后运行列出的验证并更新结果；Claude 仅作只读第二意见，采纳项必须由 Codex 独立复算。
```

## 任务结果

### 修改摘要

- 待完成。

### 验证证据

- `已验证`：基准 Fast 通过，Debug 构建 0 警告、0 错误，673/673 测试通过。
- `未验证`：实现后门禁、真实 Windows UI 与真实账户场景尚未执行。

### 文件范围

- 待完成。

### 决策与限制

- 待完成。

### 下一步

- 完成本切片后进入消息输入与 Text 发送闭环。
