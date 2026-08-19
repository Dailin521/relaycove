# Stage 24.8 — 官方式频道设置与新建话题入口（工作日志）

- Status: local candidate validated; awaiting user Visual Studio UI/interaction confirmation and new explicit commit/push authorization
- Starting point: `main@a750dcd`
- Date: 2026-08-19 CST
- Scope: `RelayCove.App` 锁定频道的新建话题与频道设置覆盖页、`RelayCove.Core` 权限模型、`RelayCove.Zulip.Client` 官方接口、相关确定性测试和交互文档；不修改 Data、Web、Realm 配置或服务端
- Official references: Zulip [`stream_settings_overlay.hbs`](https://github.com/zulip/zulip/blob/main/web/templates/stream_settings/stream_settings_overlay.hbs), [`GET /streams`](https://zulip.com/api/get-streams), [`GET /streams/{id}`](https://zulip.com/api/get-stream-by-id), [user groups](https://zulip.com/api/get-user-groups), [channel folders](https://zulip.com/api/get-channel-folders), [`PATCH /streams/{id}`](https://zulip.com/api/update-stream), read on 2026-08-19.

## 用户目标

继续按 Zulip 官方结构复刻频道区域：行内 `+` 明确表示在所点频道下新建话题；“频道设置”进入官方式频道覆盖页并接通 General 范围内的真实功能；不展示个人、订阅者、权限等本阶段未实现标签。

## 最终实现

### 锁定频道的新建话题

- 行内 `+` 沿用既有新会话弹层，但进入专用锁定态：标题为“新建话题”，显示固定 `#频道名`，隐藏私信模式、联系人、模式切换和频道选择器，并自动聚焦话题输入框。全局新建入口仍可自由选择频道，Picker 改为始终显示真实频道名。
- 取消不改变当前会话。确认只构造并选择本地 `ChannelTopic` 草稿，不调用消息发送；只有用户随后在 Composer 发送首条消息才进入既有 Realm 写入流程。

### 官方式频道设置

- “频道设置”不再先激活聊天或复用会话详情面板，而是保存菜单触发行的频道 ID 并打开标题为“频道”的独立覆盖页。宽窗口为 400 DIP 左栏加 General 详情，720 DIP 及以下切换为列表→详情并提供返回。
- 左栏支持已订阅/可用/全部、名称/说明搜索、未归档/已归档/全部筛选、名称/订阅人数/活跃度排序；行显示订阅状态、隐私/颜色、名称、说明、订阅人数和周消息量。
- General 显示隐私、名称、说明、创建者、创建日期、频道 ID、文件夹和按需频道邮件地址。名称/说明使用显式编辑弹窗；文件夹选择使用保存/取消，组织管理员可创建文件夹；邮件地址只保留在当前 ViewModel 内存并由用户明确复制。
- 前往频道关闭设置并打开目标频道记住/最新话题。订阅、退出和组织管理员归档始终使用设置页当前频道 ID；退出与归档有二次确认。覆盖层支持外点、Escape 分层关闭、子弹窗焦点进入，以及最终关闭后返回原频道更多按钮。
- 每次打开、切换频道、刷新及写入成功后重新读取权威数据。读取使用 CTS 与 generation 拒绝过期响应；快照或详情失败时保留可读状态并禁用写入口。写请求使用非取消的单次提交语义，失败显示“不会自动重试”。

### Core 与 Zulip 协议

- Core 新增频道详情、文件夹、用户组、数字/匿名 group-setting、设置限制与纯权限 evaluator。递归权限计算拒绝停用/缺失组和环，区分 metadata/content/administer/subscribe/send/create-topic；组织管理员拥有频道管理覆盖，但不会因私有频道管理权自动获得内容访问。
- `IClientSession`/`IZulipGateway` 接通设置快照、频道详情、更新、创建文件夹、邮箱和归档。Gateway 使用 `include_all=true&exclude_archived=false`、当前用户角色、包含停用组的用户组与频道文件夹构建快照；访客不请求其无权读取的用户组，权限保持失败关闭。
- 更新只发送真实变化字段；创建文件夹包含必需的说明字段；更新返回未支持参数时按协议失败处理。PATCH/POST/DELETE 不增加自动重试；邮箱不进入日志、持久状态或错误文本。register 的四项长度限制作为可空权威限制进入设置页。

## 被否决或不在范围的方案

- 不把行内 `+` 解释为创建频道，也不复制官方创建频道按钮。
- 不显示个人、订阅者、权限等未接通标签，不使用 Realm 全用户集合猜测频道成员或权限。
- 不把 `include_all=true` 误当作私有频道内容权限；管理员对私有频道的 metadata 管理能力与内容读取/发送能力分开计算。
- 不采用 Zulip Web 当前较宽的 `can_archive_stream` helper 来放宽公开 Archive API：官方接口仍明确限定组织管理员，因此客户端归档入口保持组织管理员 gate，服务器继续最终裁决。
- 不持久化设置快照、用户组、文件夹或频道邮箱，不修改 SQLite、Web 或 Realm 接口。
- 不自动重试名称、说明、文件夹、订阅、退出或归档等真实写操作。

## 确定性验证

```powershell
dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore --nologo
dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo
dotnet test tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj -c Debug --no-restore --nologo
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo
```

- App Debug build：通过，0 warning / 0 error。
- `RelayCove.Core.Tests`：116/116 通过，覆盖管理员、直接成员、匿名/嵌套/DAG 组、停用/缺失/循环组、访客、私有频道 metadata/content 分离，以及设置快照代际取消和写后本地状态。
- `RelayCove.Zulip.Client.Tests`：54/54 通过，覆盖查询参数、当前用户身份/角色一致性、访客、频道详情、两种 group-setting、文件夹、邮箱、仅发送变化字段、未支持参数失败、创建文件夹和归档端点。
- `RelayCove.App.Tests`：151/151 通过，覆盖锁定频道弹层、取消/确认零发送、设置目标频道、筛选/排序、响应式列表详情、编辑/清空文件夹、Picker item→稳定 folder ID、邮箱复制与过期结果丢弃、订阅/退出/归档目标、分层关闭、权限禁用和过期读取取消。
- `git diff --check`：通过，仅有工作树行尾转换提示。
- 两轮独立只读复核检查协议/身份/权限/重试边界与 App 目标/取消/焦点行为；修复过期频道邮箱可能晚到、`users/me` 主体/角色未严格一致、嵌套组 DAG 与子弹层外点关闭后，复核未发现剩余 P0/P1。
- 用户随后报告启动报错并授权 Agent 自行测试。离线 `NativeShellPreviewSession` 在 `DISPLAY2` 复现启动后退出；临时未处理异常记录确认 `ChannelSettingsOverlayView.xaml` 的 MAUI `Picker.SelectedValue/SelectedValuePath` 在运行加载时抛 `XamlParseException`。最终改为 MAUI 支持的 `SelectedItem` 双向绑定，并由 ViewModel 投影稳定 `DraftFolderId`；临时诊断代码已移除。
- 修复后离线预览进程保持存活且 Responding，通过 UI Automation InvokePattern（无鼠标/键盘注入）打开锁定 `#design` 的新话题弹窗、1024×768 DIP 双栏设置页和 640×768 DIP 窄屏列表。截图为 `runtime-fixed-new-topic.png`、`runtime-fixed-settings.png` 和 `runtime-fixed-settings-narrow.png`；预览会话没有网络、Realm 写入或真实凭据。
- 未运行 Fast、Full、Live、打包或真实 Realm；未启动、移动、操作或截图 MAUI 窗口。构建与 xUnit 结果不替代 Visual Studio 原生 UI 人工验收。

## 待用户 Visual Studio 验证

1. 在频道行点击 `+`：确认只显示固定频道与话题输入、输入自动聚焦；分别取消和确认，确认当前会话/本地草稿行为正确，不发送首条消息。
2. 从当前聊天以外的频道打开“频道设置”：确认目标频道正确，宽屏双栏、频道列表信息、搜索/筛选/排序和 General 布局符合参考。
3. 将窗口收窄至 720 DIP 附近：确认列表→详情、返回、筛选/排序和关闭仍可操作。
4. 打开名称、说明、新建文件夹、退出和归档弹窗后只执行取消；确认 Escape 分层关闭、焦点进入弹窗，并在最终关闭设置页后回到原三点按钮。
5. 本轮人工验收不要保存名称、说明或文件夹，不要订阅/退出/归档，不要发送首条消息，以避免真实 Realm 写入。

用户确认后补记实际人工结果；收到新的明确提交/推送指令后，才按最小 `main` Git 事务提交并非强制推送本阶段文件。
