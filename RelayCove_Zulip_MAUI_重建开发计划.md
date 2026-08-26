# RelayCove MAUI 产品与架构计划

状态：当前权威计划
版本：`2.2.0`
平台：Windows 11 x64
框架：`net10.0-windows10.0.19041.0`
更新：2026-08-26

## 1. 产品方向

RelayCove 是个人使用的 Windows MAUI Zulip 客户端。`RelayCove.App` 是唯一继续开发和发布的产品客户端；历史 `RelayCove.Web` 不再要求功能对齐。

Zulip Realm 始终是账号、权限、成员、消息和实时事件的唯一事实源。不得增加 RelayCove 服务端、代理、BFF、第二消息后端或 WebView UI。

所有 MAUI 修改直接在 `main` 进行，一次只处理用户明确提出的一个问题。UI/交互由用户在 Visual Studio 验证，确认后才提交推送。

## 2. 当前个人 MVP

### 已支持

- 单账号 Realm 邮箱密码登录；API key 保存到 SecureStorage。
- SQLite 账号隔离缓存、离线读取、历史分页和断线恢复。
- 一对一私信、self-DM、私有空话题群聊的统一会话列表。
- 文本、附件、引用、reaction、本人编辑/删除、收藏、搜索和本机清聊天缓存。
- 实时消息、权威未读、向上分页、跳到最新消息和稳定的当前会话追加。
- Windows 通知、任务栏未读、托盘闪烁/预览/点击跳转。
- Zulip 官方 presence：在线、忙碌（协议 `idle`）、离线，以及独立个人 emoji/text 状态。

### 不做

- 公开频道、命名话题、多人私信和旧频道兼容入口。
- RelayCove Web 新功能或 MAUI/Web 对齐。
- `@` 候选、typing、应用退出后的后台 push、SSO、多账号、AI、自动更新。
- Android、iOS、Mac Catalyst、Linux、MSIX、安装器和代码签名。

## 3. 架构边界

```text
RelayCove.App
  ├─> RelayCove.Core
  ├─> RelayCove.Zulip.Client ─> RelayCove.Core
  └─> RelayCove.Data         ─> RelayCove.Core

RelayCove.App ─────────────────────────────> Zulip Realm
```

| 工程 | 责任 | 禁止事项 |
|---|---|---|
| `RelayCove.App` | MAUI XAML、ViewModel、Windows 组合根和平台适配 | 不直接使用 Zulip DTO 或 SQLiteConnection |
| `RelayCove.Core` | 领域模型、reducer、用例和公开接口 | 不引用 MAUI、HTTP、JSON 或 SQLite |
| `RelayCove.Zulip.Client` | Zulip REST/事件协议和 DTO 映射 | 不保存凭据或操作数据库 |
| `RelayCove.Data` | SQLite 缓存、迁移和事务 | 不包含网络逻辑或 API key |

MAUI UI 只通过 `IClientSession` 使用业务状态。网络和数据库 I/O 不在 UI 线程执行；账号或会话切换后的晚到结果必须丢弃。

## 4. 协议与安全

- Realm 只接受规范 HTTPS origin；生产 HTTP 固定禁用自动重定向。
- 密码只进入 `/fetch_api_key` 请求，不记录、不持久化。
- API key 不进入 URL、日志、UI、异常、测试快照或发布包。
- TLS 使用系统证书链，不提供跳过校验开关。
- `401` 进入重新认证；`429` 只按服务器要求重试幂等读取。
- 消息、群创建、成员管理和其他非幂等写入绝不自动重试。
- SQLite 是可删除缓存，不是业务主库；清缓存只能删除当前账号的精确目录或会话数据。
- presence 和个人状态只保存在当前 session，不写入 SQLite。

协议判断以当前仓库测试和 Zulip 12.1 OpenAPI 为准，不能从 UI 或缓存推测服务器权限和成员关系。

## 5. 群聊规则

群聊只使用已订阅、活动、私有、非 Web 公开且 `topics_policy=empty_topic_only` 的频道，内部会话键为 `ChannelTopic(channelId, "")`，界面不显示话题。

新建群聊必须填写名称并选择至少两名其他活跃成员。群资料和成员权限以服务器读取为准；创建、邀请、移人、转让、退出和解散均不自动重试。清聊天记录只清当前账号的本机缓存，不删除服务器历史。

## 6. Windows 交互原则

- 左侧只显示统一会话时间线；置顶优先，其余按最新消息排序。
- 当前会话在底部收到消息时自然上移并显示新消息，不做整页刷新。
- 未打开对应会话并到达最新位置前，不因悬停、托盘预览或窗口焦点清除未读。
- 系统通知和托盘提醒只在应用运行期间有效。
- 一对一状态来自官方 presence；self-DM 和群聊不伪造聚合状态。
- 视觉、鼠标、键盘、焦点、字号和 DPI 由用户在 Visual Studio 做最终人工判断。

更细的现有交互以 `docs/ui/INTERACTION_SPEC.md` 为准；代码和当前运行结果优先于旧文档措辞。

## 7. 验证与发布

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
pwsh ./scripts/verify.ps1 -Mode Live
```

- `Fast`：Debug build + Core/Zulip.Client/Data/App 四个普通测试项目。
- `Full`：Release build/tests + MAUI app 自包含 publish + ZIP/运行时/秘密检查；不重复 Fast。
- `Live`：只在明确提供隔离账号、目标和真实写授权时运行；不属于 Fast/Full。

发布目标固定为 app 项目、`win-x64`、unpackaged、自包含 ZIP。ZIP 只复制运行文件、`LICENSE` 和 `THIRD-PARTY-NOTICES.md`，不包含 `docs/`。签名、安装器和干净 VM 不是当前个人 MVP 的默认发布步骤；未运行时不得声称已验证。

## 8. 文档策略

只长期维护本计划、`docs/ai/STATUS.md`、`docs/ai/WORKFLOW.md`、一个 V2 活动计划和正式 Release Notes。完成的 Stage 临时日志不长期保留，历史以 Git commit、tag 和 GitHub Release 为准。

## 9. 官方依据

- [Zulip API](https://docs.zulip.com/api/)
- [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml)
- [MAUI SecureStorage](https://learn.microsoft.com/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0)
- [.NET MAUI Windows unpackaged 发布](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli?view=net-maui-10.0)
