# 阶段 0：同步、幂等与通知规格补丁

## 任务定义

- **任务名称：** 阶段 0 — 冻结消息同步契约
- **状态：** 待开始
- **基准提交：** `f87d8212c6e8600bc5c7d26f8aec52c3ce209f51`
- **工作分支：** `agent/stage-0-sync-contract`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 4.2、8、11、12、13 节；[`Claude Max 二次评审`](../reviews/2026-07-31-claude-max-second-review.md)

### 目标

在业务代码出现前，把消息同步、发送幂等、通知处理和私有频道权限写成唯一、可编码的规格，并新增 `DEC-003`。完成后不得继续架构评审；下一任务直接创建可构建的解决方案脚手架。

### 已知事实

- `已验证`：方案已定义 `Messages.Id INTEGER PRIMARY KEY AUTOINCREMENT`、`UNIQUE(SenderId, ClientMessageId)`，且 `MessageDto` 含 `ClientMessageId`。
- `已验证`：当前方案仍使用 check-then-insert、`LastSyncedMessageId`、`IsNotified` 和孤立的 `LastNotifiedMessageId`，同步接口未定义分页快照。
- `已验证`：仓库尚无 `RelayCove.sln` 和业务代码，本任务只能执行文档一致性检查。
- `已验证`：Claude Opus Max 二次评审确认游标、来源矩阵、通知聚合、幂等和单实例 IPC 是脚手架前必须冻结的契约。

### 假设

- `假设`：第一版服务端保持 SQLite 单写者；增加写实例或更换数据库时必须重新评审同步协议。
- `假设`：`NotificationSummaryThreshold` 第一版默认 `10`，它是可测试配置，不是长期架构常量。

### 范围

- 必须实现：
  - 修订同步分页、快照上限、本地事务、single-flight 与触发原因。
  - 修订发送幂等、消息来源、通知状态和私有频道权限语义。
  - 新增 `DEC-003：消息同步、幂等与通知语义`。
  - 在工作流中增加决策记录触发条件，并清理 `CLAUDE.md` 的过时规范性表述。
- 允许修改：
  - `RelayCove_工程落地方案.md`
  - `docs/ai/DECISIONS.md`
  - `docs/ai/WORKFLOW.md`
  - `docs/ai/STATUS.md`
  - `CLAUDE.md`
  - 本任务文件
- 明确不做：
  - 不创建业务代码、`RelayCove.sln`、`verify.ps1` 或 CI。
  - 不实现搜索、附件、备份、安装器或 Windows 通知探针。
  - 不新增依赖，不优化模型路由、Second Brain 或其他 AI 治理文档。
  - 不设计消息编辑、撤回、离线远程擦除或第二种数据库。

## 冻结契约

### 同步

`LastSyncedMessageId` 统一改为 `LastSyncCursor`。客户端不得用最后一条可见消息的 `MessageId` 推算游标；`cursor` 和 `nextCursor` 由服务端解释。

```csharp
public sealed record SyncResponse(
    IReadOnlyList<MessageDto> Messages,
    long NextCursor,
    long SnapshotUpperBound,
    bool HasMore);
```

首个请求在同一服务端读事务中捕获 `SnapshotUpperBound`，并计算本页消息与 `NextCursor`。后续页携带同一上限，只扫描 `MessageId > cursor && MessageId <= SnapshotUpperBound`；不可见消息也允许服务端扫描水位前进。客户端循环到 `HasMore=false`，最终安全水位为该轮 `SnapshotUpperBound`。

每页消息、会话/未读派生状态和 `LastSyncCursor` 必须在一个本地事务提交；任一非重复错误导致整页回滚。Toast、声音和任务栏闪烁只能在提交后执行。`Startup`、`Reconnect`、`WindowActivated`、`Periodic` 是客户端 `SyncReason`；同步使用 single-flight，并发触发合并为当前轮结束后至多一次补跑。

### 来源与通知

`IncomingMessageSource` 只保留 `Realtime`、`Sync`、`History`、`SendResponse`。`NotificationPolicy` 只保留 `None`、`PerMessage`、`Summary`。

| 来源 | 本地入库 | 增加未读 | 通知 | 推进游标 | 更新会话预览 | 声音/闪烁 |
| --- | --- | --- | --- | --- | --- | --- |
| `Realtime` | 是，按唯一键合并 | 非本人且非当前前台会话 | 按策略 | 否 | 仅较新消息 | Toast 成功后尝试 |
| `Sync` | 是，整页事务 | 非本人且非当前前台会话 | 完整轮次后决策 | 随页面事务 | 仅较新消息 | 按最终策略 |
| `History` | 是，按需懒加载 | 否 | 否 | 否 | 否 | 否 |
| `SendResponse` | 插入或更新 pending | 否 | 否 | 否 | 仅较新消息 | 否 |

删除 `LastNotifiedMessageId`，将 `IsNotified` 改为唯一真源 `IsNotificationHandled`：

- `false`：尚未完成通知决策，需要恢复处理。
- `true`：Toast 已成功提交给 Windows，或已明确决定不提醒。
- Toast 临时失败保持 `false`；声音或闪烁失败只记日志，不得重复 Toast。
- 启动和完整同步结束后扫描遗留的 `false` 记录。
- 自己发送、History、当前前台会话、静音和 `NotificationPolicy.None` 均置为 `true`。

汇总阈值只统计完整同步轮次中实际符合提醒规则的候选。`Startup` 默认汇总，用户主动 `WindowActivated` 默认不弹历史通知，后台短暂 `Reconnect` 的少量候选逐条提醒，超过默认阈值 `10` 时汇总。

### 幂等与频道权限

发送消息使用 INSERT-first：新插入返回 `201`，事务提交后只推送一次；命中 `UNIQUE(SenderId, ClientMessageId)` 时回读原消息、返回 `200`、不得再次推送。服务端校验 `ClientMessageId` 为标准 `Guid`，响应继续回传该字段。

用户加入或重新加入私有频道后可通过 History/Search 查看全部历史，但不全量回填、不增加历史可见水位。添加成员、读取该频道当前最新 `MessageId`、设置 `ConversationMembers.LastReadMessageId` 必须处于同一服务端事务；加入前历史不计未读、不通知。

移除成员后，服务端通过 `ConversationAccessRevoked`、权威会话列表对账和相关请求 `403` 收敛权限。客户端联网后删除该会话、本地消息和附件缓存；离线设备无法远程擦除是第一版已知限制。通知激活探针后续固定验证 `Mutex + Named Pipe`，本任务只记录该要求。

### 决策与治理

`DEC-003` 必须记录上述服务端游标语义、SQLite 单写者前提、INSERT-first、客户端逐页事务、通知唯一真源和私有频道历史规则。`WORKFLOW.md` 的“记录与提交”必须明确：协议、数据库或兼容性发生变化时，同一任务必须更新 `DECISIONS.md`。

## 验收标准

- [ ] 旧字段 `LastSyncedMessageId`、`LastNotifiedMessageId`、`IsNotified` 不再出现在当前规范性文档中。
- [ ] `SyncResponse` 四字段、同一快照分页、客户端逐页事务和 single-flight 均有明确成功/失败语义。
- [ ] 来源行为矩阵与通知恢复规则完整，没有第二个通知真源。
- [ ] INSERT-first 的 `200`/`201`、推送次数和 `Guid` 校验可直接转成测试。
- [ ] 私有频道加入、重新加入、撤权、`403` 和在线缓存清理边界明确。
- [ ] `DEC-003` 与 `WORKFLOW.md` 决策触发条件已落盘。
- [ ] `STATUS.md` 的下一任务改为“创建可构建解决方案和真实验证脚本”。

### 验证命令

```powershell
rg -n "LastSyncCursor|IsNotificationHandled|SnapshotUpperBound|IncomingMessageSource|NotificationPolicy|DEC-003" RelayCove_工程落地方案.md CLAUDE.md docs/ai
rg -n "LastSyncedMessageId|LastNotifiedMessageId|IsNotified" RelayCove_工程落地方案.md CLAUDE.md docs/ai/DECISIONS.md
git diff --check
```

第二条命令预期无输出并以退出码 `1` 表示没有旧字段；退出码大于 `1` 才是命令错误。本任务不得声明 `dotnet build` 或自动化测试通过。

### 停止并询问

- 仓库实际 DDL 或 DTO 与“已知事实”冲突。
- 必须改变已冻结契约、引入新数据库/依赖，或私有频道历史规则出现不同产品要求。
- 发现任务范围外的未提交修改、密钥、破坏性操作或无法解释的文档冲突。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行步骤

1. 检查基准、分支、工作区和方案第 8、11、12、13 节的原文。
2. 先修订工程方案，使字段、响应、事务、状态矩阵和权限边界自洽。
3. 新增 `DEC-003`，再同步更新 `WORKFLOW.md`、`CLAUDE.md` 和 `STATUS.md`。
4. 用 `rg` 查新契约和旧字段，审阅完整差异并运行 `git diff --check`。
5. 填写下方结果；完成独立复核后才允许本地提交，不得自行推送或合并。

## 任务结果

### 修改摘要

- 待实施后填写。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | 规格一致性 `rg` | 待实施后运行 |
| `未验证` | `git diff --check` | 待实施后运行 |
| `未验证` | `dotnet build` | 本任务无解决方案，不适用 |

### 文件范围

- 新增：待实施后填写。
- 修改：待实施后填写。
- 删除：待实施后填写。

### 决策与限制

- 决策：以上“冻结契约”是本任务唯一实现口径。
- 已知限制：本任务只建立规范，不证明运行时行为。

### 下一步

- 创建 `RelayCove.sln`、四个源项目、测试项目与真实 `Fast`/`Full` 验证脚本，并完成正向和负向验证。
