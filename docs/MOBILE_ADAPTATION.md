# RichChat Android / iOS 移动适配计划

更新：2026-09-17。
状态：已建立适配方案；功能实现、移动构建、真机验证和签名分发均未开始。
核查基线：`main` / `5477b227ab2c63bb9cd16c1762b27de8073fd2f6`，Windows `1.0.9 / 16`。

本次用户授权仅为将方案落实到仓库，不代表授权修改功能、运行真实 Realm 写入、配置签名、发布安装包或修改服务器。后续实施按当次用户指令和 [AGENTS](../AGENTS.md) 执行。本计划是[产品与架构计划](../RelayCove_Zulip_MAUI_重建开发计划.md)的移动端补充，不把规划写成已支持功能。

## 1. 核心决定与范围

继续使用 MAUI + C#，继续直连现有 Zulip Realm，不更换框架、不重新开发聊天后端。保留 Core、Zulip.Client 和 Data 的职责及业务规则；主要改造 App 层的平台依赖、手机交互和生命周期。

目标为 Android 手机与 iPhone/iOS。Mac Catalyst/macOS、Linux、独立平板交互和桌面功能全量对齐不在首个手机内测包范围。大屏先保证布局不溢出，不承诺专门的 iPad 体验。

实施顺序：解除 Windows 绑定 → Android 基础聊天 → iOS 复用适配 → 各平台签名分发。后台推送独立规划，不阻塞前台聊天内测，但没有推送时必须明确提示后台/锁屏收信限制。

不能改变的边界：

- `RelayCove.App` 仍是唯一产品客户端；不恢复历史 Web，不加入 WebView、BFF、代理或第二消息后端。
- 保留当前单账号、私信/self-DM、受支持的私有空话题群聊范围；不借适配增加公开频道、SSO、多账号等功能。
- Zulip 是权威状态；SQLite 只是账号隔离缓存。API key 仅保存到平台安全存储，不进入日志、URL、缓存库、测试数据或安装包。
- 保留 HTTPS、系统证书校验、禁用自动重定向及非幂等写入不自动重试的约束。切网、恢复前台不能造成消息或群管理请求重复提交。
- 保留 Windows 现有程序标识、用户数据、交互和发布渠道；不将移动版发布到 `update-win-x64.json`，不为迁移统一修改现有标识。

## 2. 已确认的源码障碍

以下是基线源码事实，不是实际 Android/iOS 编译报错清单。后续代码变化后需重新核查。

| 位置 | 基线事实 | 适配工作 |
|---|---|---|
| [App 项目](../src/RelayCove.App/RelayCove.App.csproj) | 只有 `net10.0-windows10.0.19041.0`；`WinExe`、Windows 版本/图标配置及通知、绘图库依赖 | 增加 Android/iOS 目标；按平台隔离属性、包、资源及发布配置 |
| [Platforms](../src/RelayCove.App/Platforms) | 仅 Windows 目录 | 补 Android/iOS 启动入口、manifest/plist、必要权限和资源 |
| [MauiProgram](../src/RelayCove.App/MauiProgram.cs) | 直接注册 Windows 窗口、通知、文件保存、目录选择、开机启动、用户活动服务和自定义 Handler | 分离共享注册与平台注册，不在移动目标引用 Windows 类型 |
| [App](../src/RelayCove.App/App.xaml.cs) | 创建桌面标题栏，挂接窗口壳、通知和销毁清理 | 区分桌面窗口功能与移动前后台/页面生命周期 |
| [MainPage](../src/RelayCove.App/MainPage.xaml.cs) | 共用目录内直接使用 WinUI 类型、鼠标事件和视觉树 | 原生事件进入平台实现；手机导航与弹层改为触摸操作 |
| [MessageListView](../src/RelayCove.App/Controls/MessageListView.xaml.cs) | 滚动定位、可见区及部分动画直接操作 WinUI 控件 | 复用消息与滚动意图，分别实现原生视口行为并验证分页锚点 |
| [RealmMediaImageView](../src/RelayCove.App/Controls/RealmMediaImageView.cs) | 依赖 Windows 透明 GIF 渲染器 | 分离图片获取与原生解码/播放；补移动端停止播放和内存释放 |
| [文件选择服务](../src/RelayCove.App/Services/MauiFileSelectionService.cs) | 头像/表情类型过滤仅配置 WinUI，附件记录包含 `FullPath` | 按平台配置过滤；以可读流而不是永久绝对路径作为上传边界 |
| [存储位置服务](../src/RelayCove.App/Services/StorageLocationService.cs) | 提供桌面自定义目录及重启迁移流程 | Windows 保留；移动端首版使用应用私有目录，不直接搬用迁盘交互 |
| [README 发布说明](../README.md) | 当前仅 Windows ZIP、EXE 和 Windows 更新清单；不包含后台 push | 新增独立移动构建/分发流程，不能直接复用 EXE 更新路径 |

[Core](../src/RelayCove.Core/RelayCove.Core.csproj)、[Zulip.Client](../src/RelayCove.Zulip.Client/RelayCove.Zulip.Client.csproj) 和 [Data](../src/RelayCove.Data/RelayCove.Data.csproj) 都是普通 `net10.0` 项目，具备继续共享的工程基础。这不等于已经验证所有代码和 SQLite 原生依赖能在手机 Release 中运行。

实施前还需检查 App 的其他控件、行为、资源服务和测试项目，不能把上表当作全部 Windows 依赖。重点搜索 `Microsoft.UI`、`Windows.*`、`WinRT`、`System.Drawing`、`DllImport`、`win-x64` 以及平台实现注册，并区分合法的 Windows 专用代码与泄漏到共享层的引用。

## 3. 实现方式

### 3.1 工程与平台隔离

维持现有 MAUI 单项目和依赖方向。共享目录放页面、ViewModel 和共享服务；原生实现放 `Platforms/Windows`、`Platforms/Android`、`Platforms/iOS`。优先使用已有接口、平台分部类和小范围条件编译，不新建通用插件框架，不复制三套聊天业务。[官方单项目说明](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/single-project?view=net-maui-10.0)

目标框架、最低系统版本、RuntimeIdentifier、Windows 专用包和资源必须按目标配置。构建入口应显式指定 App 项目和平台；不能要求普通 Windows 开发每次还原 iOS 工具链，也不能要求 Mac 构建 Windows 应用。保留现有 Windows 验证命令含义；跨平台测试不默认构建包含 Windows App/原生测试的整个 solution。

现有 Windows `CreateMauiApp(StorageLocationService)` 初始化链应先梳理再抽取。移动启动也必须在打开数据库前准备好私有数据目录，但不执行桌面单实例、迁移窗口或托盘逻辑。不要仅添加 TFM 后用大量空服务掩盖运行时缺失。

桌面独有服务允许明确的“不支持”实现，但对应入口必须隐藏或禁用；文件、凭据、消息等实际承诺的能力不得返回假成功。Windows 专用输入框、表情文字和颜色选择 Handler 需要逐项决定移动替代方式，不能只取消注册后假定行为一致。

### 3.2 首个手机内测包的能力边界

| 能力 | 首个手机内测目标 | 后续或不适用 |
|---|---|---|
| 登录与会话 | Realm 登录、安全凭据恢复、注销；私信及现有受支持群聊列表 | 不新增账号类型或群模型 |
| 基础消息 | 文本发送/接收、历史分页、已读/未读、离线缓存和恢复同步 | 复杂群管理、搜索、reaction、编辑/删除等按后续任务移植，不把 Windows 已实现等同于手机已完成 |
| 图片与附件 | 选择、发送、预览、下载/导出；取消、失败和权限拒绝有反馈 | 拍照、录音、分享扩展不作为首包前置条件 |
| 表情 | Unicode/现有小表情输入；收到的图片/GIF 能正确显示 | 第三方 GIF 搜索、本地收藏导入和完整面板交互后续单独补齐 |
| 手机导航 | 会话列表 → 聊天 → 详情/设置；返回保留当前会话上下文 | 不把桌面三栏整体缩小塞入手机 |
| 存储 | 账号数据放应用私有持久目录；可淘汰图片放缓存目录 | 不支持桌面式选择磁盘和迁移缓存；已导入收藏不当作可淘汰缓存 |
| 消息提醒 | 前台会话提醒与未读状态；明确没有后台 push | 系统远程推送另行实施；不承诺锁屏/进程退出后实时提醒 |
| 桌面功能 | Windows 原样保留 | 手机隐藏托盘、任务栏角标、开机启动、EXE 更新等入口 |

这是阶段性内测范围，不代表删除 Windows 功能。每次移植后同步功能说明，列明手机已实现、待移植和不适用项；不得用“跨平台版”暗示功能已全量对齐。

### 3.3 手机交互与媒体

复用业务状态和可共享控件；允许建立精简手机页面壳，不强迫复用整个桌面 MainPage。聊天页必须处理安全区、软键盘顶起输入框、输入法候选和触摸目标大小。默认软键盘换行，使用明确发送按钮；桌面 Enter/Ctrl+Enter 规则不自动套到手机。

Android 返回键/手势先关闭当前弹层或键盘，再返回上一页面；iOS 返回导航保持可用。消息操作使用长按菜单，图片使用双指缩放和拖动；悬停不能成为 GIF 预览或操作入口的唯一方式。非输入区域的 Windows 鼠标专用约定不延伸到移动端。

消息列表必须验证向上分页不跳动、浏览旧消息不被新消息拉到底部、回到最新位置才提交对应已读。图片/GIF 离屏、换会话或退后台时释放/暂停不必要资源，避免原图并发解码挤占手机内存。不能把显示静态首帧当作 GIF 播放完成。

文件选择按 Android MIME、iOS 类型标识配置；用 `OpenReadAsync` 等流接口读取，不能依赖 `FullPath` 永远存在。跨页面/进程恢复需要先明确临时副本和授权期限。导出使用系统支持的保存/分享能力，不写死桌面下载目录。只申请实际功能需要的权限，拒绝或取消不得丢失草稿。[官方文件选择说明](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/file-picker?view=net-maui-10.0)

### 3.4 生命周期、恢复与安全

移动系统可能停止或回收应用，不能假定 long-poll 永久运行，也不能只在退出事件里保存必要状态。[官方生命周期说明](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/app-lifecycle?view=net-maui-10.0)

- 退后台：停止不必要的轮询、presence 心跳、动画和图片加载；保留可恢复的会话状态。不要把一次失焦直接当成注销或永久销毁 session。
- 回前台/切网：只允许一个有效事件接收任务；沿用既有断线恢复，队列失效时重新注册并补同步。按消息 ID 去重，校正权威未读，丢弃旧账号/旧会话的晚到结果。
- 进程被回收：从安全凭据和账号缓存冷启动；显式记录草稿/待发送状态哪些能够恢复。服务器提交结果未知的写入不能自动重发。
- 已读：必须同时满足应用前台、对应聊天可见且到达最新位置；进入后台、仅显示通知或显示会话列表不能清未读。
- presence：不移植 Windows 全局键鼠活动检测；以应用前台/交互作为移动活动来源，并尊重既有手动忙碌/离线选择，不伪造后台持续在线。

Android 需处理安全存储备份恢复后无法解密，iOS 需处理 Keychain 在重装后可能仍存在的凭据；确定首次安装、升级、注销和凭据损坏的清理边界，失败进入重新认证而不是读取未授权缓存。不能为修复安全存储而删除其他应用或整个用户目录的数据。[官方 SecureStorage 说明](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0)

待实测风险包括 SQLite 原生库、锁屏后文件访问、图片解码、Release 裁剪/AOT 下的 JSON 序列化与动态绑定。先根据真实构建警告和设备结果修正，不能用 Debug 通过代替 Release 验收，也不能默认通过关闭所有裁剪来宣告完成。[官方裁剪说明](https://learn.microsoft.com/en-us/dotnet/maui/deployment/trimming?view=net-maui-10.0)

## 4. 分阶段任务与退出条件

所有未完成项都保持未勾选。每阶段记录提交号、环境、实际命令/结果、设备证据和剩余问题；不设没有依据的工期。

| 阶段 | 任务 | 完成条件 | 当前状态 |
|---|---|---|---|
| M0 文档 | 建立本计划，登记 AI 索引，同步产品边界和日志 | 方案在仓库可查；不声称已适配 | 文档阶段 |
| M1 平台基础 | 盘点 App/测试内 Windows 依赖；隔离注册、原生控件与包；补 Android/iOS 入口和资源 | Windows 目标定向构建及受影响测试通过；Android 可启动至登录界面；iOS 构建单独记录 | 未开始 |
| M2 Android 基础聊天 | 手机页面、文件/媒体、凭据、缓存、前后台恢复及上表首包能力 | Android 真机完成安装→登录→聊天→图片附件→后台恢复→注销；Release 路径验证 | 未开始 |
| M3 iOS 基础聊天 | 复用 M1/M2 共享代码；补 iOS 导航、键盘、安全存储和文件能力 | 匹配 Mac/Xcode 环境构建；iPhone 验证同一流程，不能仅有模拟器截图 | 未开始 |
| M4 安装与分发 | 平台独立版本/签名/产物脚本与分发说明 | 对应平台签名包能够按选定渠道安装、覆盖升级和启动，结果与同一提交对应 | 未开始 |
| N1 后台推送 | 另行核查 Zulip 与自定义移动应用的推送路由、凭据、通知点击和成本 | 另行授权后定义并验证后台/锁屏场景；不能用轮询保活代替 | 独立待规划，不阻塞 M2/M3 |

M1 内部分两步：先隔离 Windows 依赖并保持 Windows 行为；再引入移动目标与登录启动链。不在同一变更中重做所有页面。缺少 Mac 时可以完成 Android 部分，但必须标记 iOS 未验证，不得将 M1 的双平台验证整体关闭。

Android 和 iOS 可分别完成 M4；不要求等 iOS 分发条件齐备才交付已验证的 Android 内测包。复杂群管理、搜索、消息操作和表情面板等后续功能按独立任务逐步移植，不混入首次平台拆分。

### 实施检查清单

- [ ] M1：核实 SDK/workload、Android SDK/JDK、Mac/Xcode 可用性，以及现有 App.Tests/原生测试的平台绑定；记录适配版本组合，不顺手整体升级依赖。
- [ ] M1：新增目标、入口、图标/启动图和平台注册，清理共享代码中的 Windows 类型依赖；Windows 回归无新增阻断。
- [ ] M2：Android 登录、会话、文本、已读/未读、分页和缓存恢复通过。
- [ ] M2：图片/GIF、附件流、权限拒绝、长按/返回/键盘及内存释放通过。
- [ ] M2：断网、切网、后台恢复、进程重建、注销及未知发送结果通过；无重复事件循环或重复发送。
- [ ] M3：iOS 完成相同功能与生命周期验证，检查 Keychain、文件访问和 Release 裁剪/AOT。
- [ ] M4：Android 签名 APK；需要商店时再生成对应 AAB，验证覆盖升级和产物信息。
- [ ] M4：iOS 签名归档/IPA 与选定分发渠道匹配，完成授权测试设备安装与升级。
- [ ] 每阶段同步功能说明、STATUS 和当月日志，保留实际证据与未完成项。

## 5. 构建环境与分发约束

当前项目仍只有 Windows 构建目标。本节描述后续要实现的流程，不是当前已经可运行的移动打包命令。

| 平台 | 环境与产物要求 | 不允许的替代说法 |
|---|---|---|
| Android | 与仓库 .NET/MAUI 匹配的 Android workload、SDK/JDK；首个内测优先可安装 APK；正式分发使用用户管理的 keystore，AAB 按渠道需要提供 | Debug APK 可运行，不等于发布签名、覆盖升级和商店分发已经完成；AAB 不是直接安装的 APK |
| iOS | Mac 与匹配的 Xcode/.NET iOS workload；可从 Windows 通过远程 Mac 构建。设备/分发签名需要相应证书、profile、Bundle ID 与渠道资格 | 不能仅在 Windows 本机独立产出正式 iOS 分发包；未签名或模拟器产物不等于可安装 IPA；IPA 不能任意下载即装 |

官方依据：[Android 命令行发布](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0)、[iOS 命令行发布](https://learn.microsoft.com/en-us/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0)、[Pair to Mac](https://learn.microsoft.com/en-us/dotnet/maui/ios/pair-to-mac?view=net-maui-10.0)。文档核查日期为 2026-09-17；实施时核实工具链兼容性，不把网页示例版本直接当作本仓库版本。

最低 Android/iOS 版本、代表真机、可用 Mac、应用标识可注册性、Android keystore、Apple 开发者资格和 iOS 分发方式尚未确认。M1 先记录已知环境；缺少签名材料只阻断对应签名/分发门槛，不阻断共享代码整理或 Android 调试。不得在仓库保存 keystore、证书私钥、profile 内个人信息、密码或设备清单；本轮不索取或创建这些材料。

移动版本/构建号按各平台升级规则维护，App ID/Bundle ID 在首次分发前确认。保留既有 Windows 标识和更新渠道；手机内测先使用明确的手动安装/平台分发方式，不实现静默更新，不复用 Windows EXE 下载器。

## 6. 最小验证与 CI

本次纯文档变更只核查源码事实、引用、范围和差异，不运行 Fast/Full、应用、打包或 Live。现有 Windows 验证规则不变；本计划不新增 workflow，也不修改验证脚本。

后续遵循“改到哪里，验证到哪里”：共享逻辑运行相关普通测试，Windows 平台改动做 Windows 定向构建与受影响测试，移动目标/页面改动做该平台构建和必要设备验证。认证、协议、同步、数据和打包变化按 AGENTS 做独立只读复核。既有 Windows 测试记录不能当作手机验收证据。

若后续增加 CI，普通 PR 仅保留相关编译与必要确定性测试，按路径及平台选择任务；不让文档改动触发全平台打包，不让每个 PR 都跑签名发布或大型设备矩阵。正式移动 Release 仍必须覆盖实际使用的裁剪/AOT、原生 SQLite 和签名安装流程。缺少构建工具的任务标为未运行/阻塞，不输出伪通过；CI 简化不取消正式交付门槛。

| 验收场景 | 需要确认的结果 |
|---|---|
| 首次启动与重启 | 权限拒绝不崩溃；凭据可恢复；损坏凭据进入重新登录；无凭据泄漏 |
| 会话与消息 | 私信/受支持群聊收发正确，分页无重复/跳位，后台或未到最新位置不误清未读 |
| 网络与生命周期 | 前后台多次切换、断网恢复、队列过期、进程重建后可继续使用；未知写入不重试 |
| 文件与媒体 | PNG/JPEG/透明 GIF、取消选择、权限拒绝、附件下载/导出正确；离屏释放资源 |
| 账号与安全 | 注销清凭据、锁定对应缓存；重装/备份恢复符合明确规则；晚到结果不串账号 |
| 发布与升级 | Release 包按渠道安装并覆盖升级；保留应保留的数据；不进入 Windows 更新清单 |
| Windows 回归 | 登录、聊天、分页、输入框、图片预览/GIF、托盘通知、缓存目录及更新流程不因平台拆分退化 |

设备覆盖至少包括每个平台的拟支持最低版本和一个较新版本；模拟器用于补充覆盖，核心安装、输入、文件和生命周期须有实际 Android 手机/iPhone 验证。设备及 OS 未实际测试时明确列为待验证。真实 Realm 写入只使用另行授权的隔离账号与目标，不擅自以生产账号发消息测试。

## 7. 下一次实施入口

取得功能实施授权后，先做 M1 的 Windows 依赖盘点与最小隔离，记录每个依赖应保留、迁移或替换的位置，并保持 Windows 构建可用。不要从加入打包脚本开始，不直接进入大规模 UI 重写，也不要自动实施 N1 推送或服务器变更。

当前可以确认的是方案已明确、源码障碍已记录；当前不能确认 Android/iOS 能编译、能安装或已完成真机适配。实际进度继续维护在本清单、[STATUS](ai/STATUS.md)与[当月工作日志](worklogs/2026-09.md)。
