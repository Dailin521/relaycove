# RelayCove 当前状态

Updated: 2026-08-26
Branch: `main`
Current baseline: `a91679106a7ad446a7f01fd6a444796172a382f4`（与 `origin/main` 一致）
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
- 私有群聊创建与群设置，所有非幂等写入保持零自动重试。
- Windows 通知、任务栏未读、托盘闪烁/悬浮预览/会话点击跳转。
- 一对一官方在线/忙碌/离线状态，以及独立的个人 emoji/text 状态；两者均为 session-only。

## 未运行或不在个人 MVP 默认门禁

- 当前文档清理未运行 Fast、Full、Live、应用启动、截图或 Realm 访问。
- `Live` 仍只允许显式隔离凭据和真实写授权；普通开发及发布不得隐式运行。
- 干净 Windows 11 VM、安装器、MSIX、签名和应用退出后的后台 push 尚未验证或不在当前范围，不能声称已支持。
- 本机 `.verify/` 目录保留，不清理、不暂存、不提交。

## 文档状态

完成的 Stage 21–38 临时日志已从当前树移除。必要历史可通过 Git commit、tag、GitHub Release 和 `docs/releases/` 查询；STATUS 不再累计逐阶段开发流水账。
