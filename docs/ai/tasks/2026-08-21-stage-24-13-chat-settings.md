# Stage 24.13 — 聊天头部与会话设置

Date: 2026-08-21
Status: local candidate; Visual Studio confirmation pending

## Problem

聊天头部同时显示搜索、详情和话题更多，动作重复；旧详情面板又偏向内部能力说明。用户要求所有聊天头部只保留搜索和右侧设置，并以接近微信的面向用户结构展示频道（界面称群聊）或一对一私聊设置。本项目本轮不增加多人私信功能。

这是本轮唯一问题。Stage 24.11 与 Stage 24.12 的未提交候选保留在当前工作树；Stage 24.13 取代 Stage 24.12 的详情面板呈现。没有修改 RelayCove.Web、Zulip 消息协议或服务端消息。

## Implementation

- 聊天头部移除中间详情按钮和话题更多按钮，只保留搜索与右侧省略号设置；设置面板仅在用户点击该按钮后打开，旧“宽屏默认显示详情”偏好及自动打开路径已移除。话题行自身的更多菜单保持不变。
- 频道设置显示权威频道成员及头像、频道名、频道说明（界面称群公告）、账号隔离的本机备注、频道静音、频道置顶、退出频道和清聊天记录。
- 成员列表同时读取频道成员 ID、当前账号可见的 Realm 用户目录和频道详情；任何成员 ID 缺少用户映射时失败关闭，不用当前消息窗口猜测成员。
- 一对一私聊设置只显示对方头像、消息免打扰和置顶。免打扰与置顶按账号和规范私聊键保存在本机；置顶会调整本机私信列表顺序，免打扰以弱化列表行表达本机偏好。本轮未增加多人私信设置或成员管理。
- “清聊天记录”只删除当前规范会话的 SQLite 消息缓存。频道聊天的真实会话键是 `ChannelTopic(channelId, topic)`，因此确认文案明确为“当前话题”；不会删除服务器消息、订阅、未读元数据、账号凭据或其他话题/私聊。重新进入后仍从 Zulip 同步。
- 清除前取消并换代当前历史加载；过期网络页在清除或会话切换后不能再次写回缓存，也不能覆盖当前投影。

## Deterministic evidence

```powershell
dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-13-build/
# passed; 0 warnings, 0 errors

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-13-app-tests/
# 210/210 passed

dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo -p:BaseOutputPath=.verify/stage24-13-core-tests/
# 131/131 passed

dotnet test tests/RelayCove.Data.Tests/RelayCove.Data.Tests.csproj -c Debug --no-restore --nologo -p:BaseOutputPath=.verify/stage24-13-data-tests/
# 25/25 passed
```

`pwsh ./scripts/verify.ps1 -Mode Fast` 已尝试，但在编译/测试前被仓库级 `dotnet format --verify-no-changes` 门禁阻止：当前 checkout 的 `core.autocrlf=false`，且包括未修改的 `AnonymousChannelGroupSetting.cs`、`ComposerEditorHandler.cs` 和 `TopicPermalinkTests.cs` 在内的大量文件为 LF，而 `.editorconfig`/`.gitattributes` 要求 CRLF；另有未修改的 `ComposerEditorHandler.cs` 导入顺序报告。没有为本轮批量改写这些无关文件。未运行 Full、Live、打包、生产 Realm 或真实写入。未启动、移动、操作或截图用户的 MAUI 窗口。

独立只读复核最初发现两个 P1：历史页通过代次检查后仍可能与清除形成 TOCTOU，以及不支持的 group/self-DM 可打开空设置面板。最终实现用同一命令屏障串行“当前代次检查 + 历史落盘”与清除，并将设置入口限制为频道或 exactly-one-other-user 私聊；新增竞态、群组私信禁用、成员映射失败、会话切换晚到结果和双账号隔离回归。两项定向复核确认原问题均已关闭，最终无剩余 P0/P1/P2。

## Visual Studio short check

1. 打开任一频道话题：头部动作应只有搜索和右侧省略号设置；设置只在点击省略号后显示。
2. 频道设置应显示全部成员头像、频道名、群公告、备注、消息免打扰、置顶聊天、退出群聊和清聊天记录；窄窗口可滚动到底且确认层不被裁切。
3. 点击“清聊天记录”应先出现“只清当前话题本机缓存、不删除服务器消息”的确认；确认后当前消息清空，切走再进入会重新同步。
4. 打开一对一私聊设置：应只显示对方头像、消息免打扰和置顶；置顶后该私聊位于本机私信列表顶部。
5. 切换另一个会话：旧设置面板应关闭，不得闪回上一频道的成员或公告。

Manual result: pending user Visual Studio confirmation.
