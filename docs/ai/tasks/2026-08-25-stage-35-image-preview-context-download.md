# Stage 35 — 图片消息纯预览与右键下载

Status: completed and accepted by user in Visual Studio

## Scope

- 图片附件在消息气泡中只显示受控预览框，不再常驻显示文件图标、文件名、类型和下载按钮。
- 当一条消息只有图片附件时，同时移除外层蓝色消息气泡的背景、边框和内边距，只保留图片预览框；图文混排和普通文件消息仍使用正常气泡。
- 左键图片继续打开现有遮罩预览；右键图片打开现有消息菜单，并额外显示“下载原图”。
- 普通文件附件继续显示文件卡片和下载按钮；不改附件解析、受控媒体读取、保存流程或服务器消息内容。

## Diagnosis

- 图片与普通文件共用同一附件模板。图片预览虽然受 `IsImage` 控制，但其下方的文件资料 Grid 没有类型可见性条件，因此每张图片都固定带一整行文件资料和下载按钮。
- 消息行现有右键行为只携带 `MessageItem`，无法知道鼠标是否命中了某一张图片，所以正常消息菜单没有条件展示图片下载动作。
- 被否决的方案包括保留常驻下载区、用独立且只有下载项的原生菜单替换消息菜单，以及根据一条消息是否含图对整行右键菜单无条件加下载。这些方案分别不符合纯预览要求、丢失正常消息动作，或无法在多附件消息中确认下载目标。

## Final implementation

- 文件资料 Grid 仅在 `IsFile` 时显示；图片附件保留原有外框和 220 DIP 受控预览，左键预览不变。
- `MessageItem.IsImageOnly` 只在无正文、无引用且附件全部是图片时成立；对应气泡改为透明、零边框和零内边距。正文、引用或普通文件任一存在时不应用该视觉覆盖。
- 图片预览新增 Windows 右键命中行为，从当前虚拟化行同时解析真实 `MessageItem`、被点击的 `MessageAttachmentItem` 和页面坐标，并阻止外层消息右键再打开第二次菜单。
- `ShellViewModel` 为当前消息菜单保存可空图片附件。只有从图片右键进入时，现有菜单顶部显示“下载原图”，其余收藏、引用、编辑/删除、复制和在 Zulip 打开等正常动作保持不变；从消息其他位置打开菜单时该项隐藏。
- 下载仍复用现有受控 Realm 媒体读取和本机保存流程。选择下载后先关闭菜单并恢复焦点，不自动重试或暴露远端地址。

## Deterministic validation

- `dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ImageAttachments_WhenRendered_ShowOnlyPreviewAndAddDownloadToMessageContextMenu|FullyQualifiedName~OpenImageAttachmentMenuAtCommand_WhenInvoked_StoresImageAndKeepsNormalMessageActions|FullyQualifiedName~DownloadAttachmentCommand_WhenStartedFromImageMenu_ClosesMenuBeforeSaving|FullyQualifiedName~DownloadAttachmentCommand_WhenControlledReadSucceeds_SavesExactBytes" -p:UseAppHost=false -p:OutputPath=.verify/stage35-image-preview-tests/` — passed 4/4.
- `dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug -p:UseAppHost=false -p:OutputPath=.verify/stage35-image-preview-build/` — passed with 0 warnings and 0 errors.
- 用户首次检查确认图片资料区已经移除，但指出纯图片消息仍保留蓝色外层气泡。补充的纯图片识别与气泡触发器定向测试通过 2/2；修正后的 App Debug 构建通过，0 warnings、0 errors。

Not run: complete App suite, Fast, Full, Live, package, Agent app startup, screenshot, Realm access or external write.

## Manual result

- 用户在 Visual Studio 中复验纯图片消息，确认蓝色外层气泡已移除，图片预览及其右键菜单行为正常，并明确回复“已验证没问题”。

## Shortest manual check

1. 打开一条纯图片消息，确认只剩图片预览框，外层没有蓝色气泡、额外内边距、文件名、类型、图标或常驻下载按钮；左键图片仍能打开预览。
2. 右键图片，确认菜单顶部有“下载原图”，并且收藏、引用、复制等正常消息动作仍在；右键同一消息的文字或空白位置时不显示“下载原图”。
3. 点击“下载原图”并完成或取消保存，确认菜单关闭；再检查一条普通文件附件仍保留原文件卡片和下载按钮。
