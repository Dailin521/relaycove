# 阶段 11：最小管理员控制面

## 任务定义

- **任务名称：** 阶段 11 — 用户、频道、服务器状态与附件上限管理
- **状态：** `进行中`
- **基准提交：** `893f68e2793c020ac15fed56a74e7edbae5bb0df`
- **工作分支：** `agent/stage-11-admin-control`
- **相关方案章节：** 8 管理员接口、10.2 `AppSettings`、17、阶段 11、24.1；M5 Gate

### 目标

补齐阻断小团队内部 RC 的最小管理能力：数据库当前管理员可在 Windows Client 内维护账号、频道、私有成员、服务器状态和单附件上限；普通用户看不到入口且服务端始终独立授权。完成后立即回到已启动的香港 VPS/双客户端 M5 Gate。

### 已知事实

- `已验证`：当前只有管理员创建用户、频道创建和私有成员 API；没有用户列表/禁用/重置/删除、频道改名/删除、状态/设置 endpoint 或 WPF 管理入口。
- `已验证`：用户硬删会被 Conversations/Message/MessageMention/Attachment 的历史 `Restrict` 外键阻止；频道已有 `Rename`、`MarkDeleted`，查询和消息投递已排除 deleted。
- `已验证`：JWT 每个 HTTP 请求动态拒绝 disabled user，新消息投递也按当前 active user 过滤；但密码重置不撤销既有 access token，已连 SignalR 不会因数据库禁用立刻关闭。
- `已验证`：附件 route、实体与 Nginx 已冻结 `100 MiB + 64 KiB` 绝对天花板；实际 25 MiB 业务上限当前只来自启动 `UploadOptions`，适合由 DB effective value 在此硬上限内进一步收紧。
- `已验证`：M5 VPS 已通过真实只读 SSH 预检，目标为 Ubuntu 22.04 x86_64，systemd/Nginx 正常且 RelayCove 尚未部署；本任务不改远端。
- `已验证`：Claude #84 按关键安全/数据库决策唯一一次调用，但当前 Desktop 仍暴露旧 `$0.5` budget wrapper，答案前失败；按策略不重试，由 Codex reviewer、真实 SQLite/HTTP/SignalR/WPF 测试裁定。

### 决策边界

- `DELETE /api/admin/users/{id}` 是不可恢复的**逻辑退役**：设置 `RetiredAt`、disabled、撤销 token 并保留用户名、显示名、消息、附件和外键历史；不伪称物理擦除，不允许恢复、登录或重用用户名。
- User 增加单调 `AccessTokenVersion`。登录/refresh 签发版本 claim；缺失 claim 视为版本 0 以兼容现有会话。禁用、恢复、重置和退役在同一 Serializable 事务内递增版本并撤销全部 refresh token，防止旧 access/refresh token 在恢复后复活。
- 禁止自禁用、自退役和并发移除最后一个 active administrator。提交后 best-effort 发布账户撤权事件，Client 收到后结束 runtime、清凭据并回到登录；事件失败不回滚，HTTP DB gate、token version 和 active-recipient 查询仍是安全真源。
- 频道改名/软删除只允许数据库当前全局管理员；Direct 不可改。删除先提交 `IsDeleted`，再向 Public 的 active users 或 Private 当前成员 best-effort 发布既有 conversation revocation，客户端复用已有 purge；事件失败由权威列表/403 收敛。
- `AppSettings` 只新增当前所需 key。上传上限为 1..100 MiB 的十进制字节值；每个上传请求在读取 body 前取得一次 DB/default snapshot，进程重启保持。配置仍是无 DB row 时的默认值，100 MiB route/实体/Nginx 是不可突破的硬上限。
- 状态只返回版本、启动时间/运行时长、连接数、DB 文件总字节、附件目录字节、effective 上传上限及最近一次脱敏错误类别/时间；不返回路径、host、连接串、异常消息、stack、用户名、token 或文件名。

### 范围

- 必须实现：
  - Shared 管理 DTO/请求与稳定错误状态。
  - Server 用户 list/create/disable/restore/reset/logical-retire、token version 与提交后账户撤权。
  - Server 全局频道目录、rename/soft-delete；复用既有 create/private-member endpoint。
  - `AppSettings` migration、持久 effective upload limit、Server status/连接计数/脱敏最近错误。
  - Client `/me` 管理能力探测、认证 transport/coordinator、单窗管理员 overlay，覆盖用户、频道、成员、状态和上传上限。
  - 真实 SQLite/HTTP/SignalR、Client transport/coordinator/WPF UIA、Fast/Full 与独立复核。
- 允许修改：
  - `src/RelayCove.Shared/`、`src/RelayCove.Server/`、`src/RelayCove.Client/` 及对应测试、migration、任务/决策/状态文档。
- 明确不做：
  - 硬删历史用户/消息/附件、在线角色编辑、审计后台、批量操作、复杂图表、文件日志 provider、外部监控、Web 管理台、市场级 RBAC 或多实例设置同步。
  - 把普通管理员身份写入 JWT 作为授权真源，或让 WPF 隐藏代替 Server policy/事务内复核。

### 验收标准

- [ ] 普通用户入口不可见且全部 admin endpoint 为 403；管理员 list/create、disable/restore、reset、logical-retire 成功，密码/请求/敏感字段不出日志或响应。
- [ ] 并发禁用/退役不能移除最后 active admin，自操作被拒；全部 refresh token 与旧 access token 失效，账户撤权事件在真实 SignalR Client 收敛到登录页且发布失败不回滚权威状态。
- [ ] 管理员可列出/创建 Public/Private、改名、软删除并管理 Private 成员；Direct/非法类型拒绝，删除后 HTTP/SignalR/cache 权限立即收敛。
- [ ] Server status 不泄露敏感信息，连接计数不负数；上传上限更新持久化、重启保持，并在 1/上限/上限+1/100 MiB 边界真实 streaming fail-closed。
- [ ] WPF 管理 overlay 可完成所有基础维护，401/403/账号切换/注销同步清状态；操作单飞、密码不回显、UIA 名称和 live region 可用。
- [ ] migration up/down/model drift、定向、Fast、Full、八项目漏洞、format/空白和至少两路 Codex 独立复核通过；M5 未验证项保持诚实记录。

### 验证命令

```powershell
dotnet test tests/RelayCove.Server.Tests/RelayCove.Server.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Admin|FullyQualifiedName~Conversation|FullyQualifiedName~Authentication|FullyQualifiedName~Attachment"
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Debug --filter "FullyQualifiedName~Admin"
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 需要物理清除有历史引用的用户/内容、跨实例动态设置、外部监控/日志系统或不可恢复的真实生产数据变更才能验收。
- 剩余 Codex 额度低于 15% 时按用户要求中止并保留现场；约每 10 分钟复核。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md`；绿色 push/仅快进集成已获授权。

## 执行提示词

```text
以约20人内部RC为尺度并行完成用户、频道/状态/设置和Client管理员overlay。硬删改为明确的不可恢复逻辑退役；所有写操作用Server policy+事务内actor复核，token版本和refresh撤销保证恢复后旧会话不复活。不要引入Web后台、复杂RBAC或市场级运维。
```

## 任务结果

`进行中`。
