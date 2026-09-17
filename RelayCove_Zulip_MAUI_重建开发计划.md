# RichChat MAUI 产品与架构计划

状态：当前权威计划
源码版本：`1.0.7`（构建号 `14`）
平台：Windows 11 x64
框架：`net10.0-windows10.0.19041.0`
更新：2026-09-14

## 1. 产品方向

RichChat 是个人使用的 Windows MAUI Zulip 客户端。为保持升级与源码兼容，工程、命名空间、应用 ID 和本机缓存键继续使用既有 `RelayCove.*` 标识；`RelayCove.App` 是唯一继续开发和发布的产品客户端，历史 `RelayCove.Web` 不再要求功能对齐。

Zulip Realm 始终是账号、权限、成员、消息和实时事件的唯一事实源。不得增加 RichChat 服务端、代理、BFF、第二消息后端或 WebView UI。

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
- 历史 RelayCove Web 新功能或 MAUI/Web 对齐。
- `@` 候选、typing、应用退出后的后台 push、SSO、多账号、AI、静默安装更新。
- Android、iOS、Mac Catalyst、Linux、MSIX 和代码签名。

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
- 当前个人 MVP 的 Realm 请求固定直连，不继承系统或环境 HTTP 代理；需要代理才能访问的网络暂不支持。TLS 仍使用系统证书链，失败后不通过切换线路重发业务写入。
- 密码只进入 `/fetch_api_key` 请求，不记录、不持久化。
- API key 不进入 URL、日志、UI、异常、测试快照或发布包。
- TLS 使用系统证书链，不提供跳过校验开关。
- `401` 进入重新认证；`429` 只按服务器要求重试幂等读取。
- 消息、群创建、成员管理和其他非幂等写入绝不自动重试。
- 上传后写入消息的附件 URL 必须保留服务器返回的 Unicode 路径；Zulip 12.1 按字面 path_id 关联附件，不能用 AbsoluteUri 将中文重新编码。普通上传与 TUS 完成/恢复统一处理，只在同源永久上传地址校验后安全序列化；保留特殊分隔符的转义，不能整体解码 URL 或用显示文件名重建路径。下载时的 HTTP URI 编码独立处理。
- SQLite 是可删除缓存，不是业务主库；清缓存只能删除当前账号的精确目录或会话数据。
- presence 和个人状态只保存在当前 session，不写入 SQLite。

协议判断以当前仓库测试和 Zulip 12.1 OpenAPI 为准，不能从 UI 或缓存推测服务器权限和成员关系。

## 5. 群聊规则

群聊只使用已订阅、活动、私有、非 Web 公开且 `topics_policy=empty_topic_only` 的频道，内部会话键为 `ChannelTopic(channelId, "")`，界面不显示话题。

新建群聊必须填写名称并选择至少两名其他活跃成员。群资料和成员权限以服务器读取为准；创建、邀请、移人、转让、退出和解散均不自动重试。清聊天记录只清当前账号的本机缓存，不删除服务器历史。

## 6. Windows 交互原则

- 左侧只显示统一会话时间线；置顶优先，其余按最新消息排序。
- 当前会话在底部收到消息时自然上移并显示新消息，不做整页刷新。
- 启动恢复先显示已解锁的账号缓存并打开首个会话，网络连接与最新消息校验在后台进行；SQLite 页读到即显示，相同消息复用原行且不重复滚动。离线仍可翻阅本地历史，恢复联网不打断已经开始的向上浏览。
- 自定义头像优先；明确来自 Zulip Jdenticon 的默认头像统一显示本地蓝底白色首字。Zulip 12.1 只在 register 顶层提供本人头像来源，普通用户快照不提供来源；不得根据与上传头像同形的 URL 猜测。已知来源随用户缓存持久化，并处理实时头像变更。
- 界面与托盘共用按账号隔离的头像文件缓存：先读本地，后台合并同一地址的下载，内容相同不重写文件或更换图片对象；刷新失败保留本地图，重连后补取。账号切换丢弃晚到结果，注销与清本地缓存清除当前账号头像；头像来源与 URL 的更新也同步到已打开的成员与联系人视图。
- 未打开对应会话并到达最新位置前，不因悬停、托盘预览或窗口焦点清除未读。
- 发送消息显式提交 `read_by_sender=true`；本人消息也保留服务端 `read` 标志。自动已读只提交当前会话最近 50 条已加载消息中的未读 ID，服务端确认同一批 ID 后才更新本地；失败后在新的可见周期允许重试。注册快照中的明确未读 ID 必须修正旧缓存误标的已读，之后恢复快照权威计数，避免范围错配和重复累计。
- 系统通知和托盘提醒只在应用运行期间有效。
- 一对一状态来自官方 presence；self-DM 和群聊不伪造聚合状态。
- 自动忙碌按 Windows 当前登录会话的键盘鼠标输入计算：连续 5 分钟无输入上报 `idle`，恢复输入后上报 `active`；每 5 秒检查本机输入，仅变化时额外上报，保持原有约 60 秒 presence 心跳。切换到其他软件使用电脑仍算活跃，不专门检测锁屏或解锁；手动忙碌、离线优先，确认成功后才更新本机状态。输入检测失败保留最近观测，首次无法判断时保守上报忙碌。
- 视觉、鼠标、键盘、焦点、字号和 DPI 由用户在 Visual Studio 做最终人工判断。
- 按用户 2026-09-08 要求，会话列表、消息列表、菜单和其他非输入区域统一只用鼠标选择与操作，原生列表键盘导航也禁用；鼠标选中的消息正文保留 Ctrl+C 复制。文本框内保留打字、光标/选区、剪贴板、输入法、Enter 发送/搜索与 Ctrl+Enter 换行；Tab 不移出输入框，Windows 系统窗口快捷键保留。

更细的现有交互以 `docs/ui/INTERACTION_SPEC.md` 为准；代码和当前运行结果优先于旧文档措辞。

### 表情包交互约定

输入区表情面板提供搜索、默认与收藏三页。默认页保持组织小表情插入光标位置；搜索/收藏中的大图独立上传、独立发送，不消费文字和附件草稿。准备或上传时切换账号/会话取消发送；消息请求已发出后的未知结果不自动重试。表情不会混入消息 reaction 选择器。

收藏成功不在聊天区显示提示卡片。

公开搜索使用 ChineseBQB 的名称与分类索引，不提供图片文字识别。独立无凭据客户端只允许同一 HTTPS 来源的目录与媒体路径；目录最多缓存 24 小时、媒体缓存预算 200 MiB、最多四路图片下载。收藏为按账号隔离的本机原始副本，按内容哈希去重，不参与缓存淘汰，不在注销时删除。支持 PNG/JPEG/WebP/GIF，单张上限 25 MiB；发送同时遵守 Realm 上传限制。聊天图片收藏通过已有受保护媒体会话读取，不能转交第三方客户端。GIF 不转静态图，面板悬停预览，卸载时停止播放。

## 7. 验证与发布

客户端默认在每次完整启动后异步检查 GitHub `Dailin521/relaycove` 的正式 Release，不依赖 Realm 登录。每个新构建只在窗口可见时提醒一次，可在通用设置关闭，关于页保留手动检查、下载和打开安装包入口；下载完成不自动安装或重启。

更新使用独立、无 Realm 凭据的 HTTP 客户端，禁用自动重定向并逐跳校验 GitHub HTTPS 下载域名。仅接受同一正式 Release 中匹配的 `update-win-x64.json` 和 Windows x64 安装器，核对产品、版本、构建号、文件名、长度和 SHA-256；构建号使用 `ApplicationVersion`，不按历史 `2.4.0` 与当前 `1.0.x` 显示版本比较。安装器脚本从已验证 ZIP 内程序的文件版本核对构建号，拒绝把旧载荷标成新构建。每次正式发布递增构建号并上传配套清单。

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full
pwsh ./scripts/verify.ps1 -Mode Live
```

- `Fast`：Debug build + Core/Zulip.Client/Data/App 四个普通测试项目。
- `Full`：Release build/tests + MAUI app 自包含 publish + ZIP/运行时/秘密检查；不重复 Fast。
- `Live`：只在明确提供隔离账号、目标和真实写授权时运行；不属于 Fast/Full。

发布目标固定为 app 项目、`win-x64`、unpackaged、自包含 ZIP。ZIP 只复制运行文件、`LICENSE` 和 `THIRD-PARTY-NOTICES.md`，不包含 `docs/`。用户于 2026-09-07 明确要求可安装程序后，增加 Inno Setup 当前用户安装器，使用 `scripts/package-installer.ps1` 包装 Full 生成且哈希匹配的 ZIP。安装到当前用户目录，提供快捷方式与卸载入口；卸载不清理账号凭据或缓存，不自动启动应用。签名和干净 VM 仍不是默认发布步骤；未运行时不得声称已验证。

安装器选择自动关闭时，通过 Restart Manager 强制结束占用目标安装目录 `RichChat.exe` 的旧进程，以兼容关闭后仍驻留托盘的旧版本；不得按进程名全局终止其他目录的程序。安装后不自动重启，升级前应先处理未发送草稿。修改此策略时运行 `scripts/test-installer-close.ps1 -IsccPath <ISCC路径>`，并使用 `-RejectShutdownQuery` 复验明确拒绝关闭的场景；脚本仅安装隔离测试文件，不操作用户应用。

## 8. 文档策略

只长期维护本计划、`docs/ai/STATUS.md`、`docs/ai/WORKFLOW.md`、一个 V2 活动计划和正式 Release Notes。完成的 Stage 临时日志不长期保留，历史以 Git commit、tag 和 GitHub Release 为准。

## 9. 官方依据

- [Zulip API](https://docs.zulip.com/api/)
- [Zulip 12.1 OpenAPI](https://github.com/zulip/zulip/blob/12.1/zerver/openapi/zulip.yaml)
- [Zulip 12.1 附件路径提取](https://github.com/zulip/zulip/blob/12.1/zerver/lib/markdown/__init__.py#L314-L330) 与[附件关联](https://github.com/zulip/zulip/blob/12.1/zerver/actions/uploads.py#L40-L82)
- [MAUI SecureStorage](https://learn.microsoft.com/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0)
- [.NET MAUI Windows unpackaged 发布](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli?view=net-maui-10.0)
