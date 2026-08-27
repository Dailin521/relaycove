# RelayCove 当前状态

Updated: 2026-08-27
Branch: `main`
Current baseline: `main`（精确提交以 Git 历史为准）
Current release: [`v2.2.0`](https://github.com/Dailin521/relaycove/releases/tag/v2.2.0)

## 产品状态

- RelayCove 当前只继续维护 Windows MAUI 客户端；历史 Web 不再参与发布或功能对齐。
- 产品使用统一的一对一/self-DM/私有空话题群聊会话列表。
- Stage 31–36 已随 `v2.1.0` 发布；Stage 37 当前会话实时追加和 Stage 38 官方 presence/个人状态已随 `v2.2.0` 发布。
- 用户已经通过 Visual Studio/实际工程确认当前 UI 与交互，Stage 38 不再保留为待确认任务。
- 唯一活动计划是 `tasks/2026-08-25-v2-optimization-plan.md`；后续仍一次只处理一个明确问题。

## 最近验证证据

`v2.2.0` 候选提交为 `5d880d78921f496450d6de409a4946d396d0d3d2`。

- `pwsh ./scripts/verify.ps1 -Mode Full` 通过。
- Release build：0 error，7 个既有 XamlC binding-performance warning。
- Core 166/166、Zulip.Client 135/135、Data 35/35、App 321/321 通过。
- app-only 自包含 publish、运行时/内容检查、秘密扫描、ZIP 与 manifest 通过。
- 发布 ZIP：96,765,856 bytes，SHA-256 `E560BA62C26EF882249C4ADD9F180597B79DB6F425425B09957C03890FDF6283`。
- GitHub Release 为正式 Latest，上传 ZIP 与本地候选一致。

这些结果只属于 `v2.2.0` 候选。当前文档清理不改代码，因此未重新运行构建或测试。

## 当前能力摘要

- 登录、凭据恢复、SQLite 缓存、历史分页、实时事件、已读/未读和断线恢复。
- 文本/附件、引用、reaction、编辑/删除、收藏、搜索、表情、文本选择和图片预览。
- 文本发送立即清空已提交输入并显示本地消息；服务器确认通过稳定 local ID 原地收敛，不重复插入或自动重发。
- 私有群聊创建与群设置，所有非幂等写入保持零自动重试。
- 所有已连接账号都可进入私有群聊创建并提交一次请求，最终权限由 Zulip 服务器权威判定。
- Windows 通知、任务栏未读、托盘闪烁/悬浮预览/会话点击跳转。
- 主窗口 `X` 隐藏窗口并移除任务栏按钮，进程继续驻留托盘；托盘右键“退出 RelayCove”才执行完整关闭清理。
- 一对一官方在线/忙碌/离线状态，以及独立的个人 emoji/text 状态；两者均为 session-only。
- 私聊与私有群聊保留最多 6 个独立原生消息视图；返回缓存会话不重建消息行和头像，内容未变化时不重复滚动，带回新消息时仍定位到最新消息。
- 附件下载支持进度、取消、重试、默认下载目录与逐次询问；标题栏下载中心按账号保留最近 20 条成功记录，并可打开文件或所在文件夹。
- 附件上传显示进度；大文件使用 Zulip 官方 TUS 分块上传，并在连接中断后按服务器权威偏移继续，非幂等创建请求仍不自动重发。

## 未运行或不在个人 MVP 默认门禁

- 历史消息滚动批次移除手动“加载更早消息”入口，改为触顶继续上滚时加载；连续跨两页真实副屏验证中，分页前锚点保持原屏幕位置，加载后滚轮继续向更早消息移动且不跳回。App 349/349 通过，独立 Debug 构建 0 warning/0 error，用户已确认可提交。
- 当前 V2 发送批次定向验证为 Core 168/168、App 330/330，最终 App 独立 Debug 构建 0 warning/0 error；真实发送与动画由用户在 Visual Studio 验证。关闭到托盘与右键退出批次 App 335/335 通过，并由用户确认交互。会话缓存切换批次 App 342/342 通过，私聊无感切换与群聊无冗余滚动已由用户在 Visual Studio 确认。
- 文件传输与下载中心批次使用独立输出目录通过 Core 170/170、Zulip.Client 144/144、Data 35/35、App 376/376，共 725 项；未运行 Live，也未由 Agent 发起真实 Realm 写入。用户已确认按当前效果提交。
- 当前批次未运行 Fast、Full、Live、应用截图或 Agent 发起的 Realm 写入。
- `Live` 仍只允许显式隔离凭据和真实写授权；普通开发及发布不得隐式运行。
- 干净 Windows 11 VM、安装器、MSIX、签名和应用退出后的后台 push 尚未验证或不在当前范围，不能声称已支持。
- 本机 `.verify/` 目录保留，不清理、不暂存、不提交。

## 文档状态

完成的 Stage 21–38 临时日志已从当前树移除。必要历史可通过 Git commit、tag、GitHub Release 和 `docs/releases/` 查询；STATUS 不再累计逐阶段开发流水账。
