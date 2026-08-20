# Stage 24.10 — 完整频道设置与现代极简界面

Date: 2026-08-19
Status: local candidate; deterministic verification complete; Visual Studio confirmation pending

## Problem

Stage 24.8 只接通了频道设置的 General 页面。用户要求继续以 Zulip 12.1 官方频道设置为功能和信息架构依据，补齐缺失能力，同时明确不复制官方旧版密集灰色皮肤，保留 RelayCove 现有的现代极简外观。

本阶段是一个频道设置问题：设置页顶部的 `+` 是创建频道；频道导航行的 `+` 仍是新建话题，语义不得混用。

## Final implementation

- 宽窗口使用频道目录/详情双栏，窄窗口使用目录→详情返回；两种布局共享详情内容。列表模式、搜索、归档筛选、名称/订阅人数/活跃度排序在两种宽度均可达。
- 频道行显示订阅、隐私/颜色、名称、说明、人数和周活跃度；当前目录模式、频道行和右侧分页都有明确的现代极简选中态。
- General 保留名称、说明、创建者/日期/ID、文件夹和按需邮箱。Personal 的页面常驻项收敛为当前色块与“修改颜色”，入口旁锚定的弹层提供频道标题实时预览、官方 4×6/24 色色盘和可展开的 Windows 原生自定义取色器；只有顶部确认写入，外点和 `Escape` 均回滚草稿。静音、置顶、桌面/声音/推送/邮件/通配符通知保持可达。
- Subscribers 同时读取权威成员 ID 与当前账号可访问的组织用户目录；已订阅者和可添加候选明确分区，候选支持姓名/邮箱搜索，添加和移除使用独立 capability，移除经过带姓名的确认，操作捕获设置页当前频道 ID。
- Permissions 接通公开/私有/Web 公开、历史共享、默认频道、四种话题策略、Realm 默认/无限/正整数天保留，以及 11 个频道 group-setting。命名组以 exact old/new 更新；匿名组保持只读、失败关闭。
- 设置页顶部创建频道支持三种隐私。公开和 Web 公开强制共享历史；私有频道可选择历史是否对后续订阅者可见。取消零写入，成功后重新加载并选中新频道。
- 归档和取消归档使用同一动态入口，只对组织管理员开放并二次确认。内容访问类高级设置还要求实际频道内容访问，只有 metadata 管理权时保持只读。子弹窗、外点和 `Escape` 只关闭最顶层，最终关闭设置页后恢复频道菜单焦点。

## Protocol and safety corrections

- `topics_policy` 改为官方字符串强类型，而不是数字；保留策略分别发送 `realm_default`、`unlimited` 或正整数天。
- 单频道 personal PATCH 不支持以 `null` 恢复 Realm 默认，因此 UI 只读取继承态，写入仅发送显式布尔值。
- 成员响应必须存在合法 `subscribers: integer[]`；缺失、别名、坏元素全部作为协议错误，不把空数组当权威成功。
- 组织用户目录必须存在合法 `members`，每个用户的 ID、姓名、邮箱、活跃与 Bot 标记均严格解析；重复 ID、坏项、unsupported 参数或成员 ID 无用户映射均失败关闭，不回退到聊天状态中的部分用户。
- 添加/移除订阅者前按频道 ID 重新读取未归档频道的权威名称；POST 添加发送对象数组，DELETE 移除发送名称字符串数组，两种非幂等写均不自动重试。
- 设置快照同时读取 `/streams` 与 `/users/me/subscriptions?include_subscribers=false`，严格验证根字段、频道 ID、重复和跨响应引用后再合并 `IsSubscribed`；Core 不再以可能滞后的本地订阅缓存覆盖该权威结果，只在两者一致为已订阅时补本地颜色。
- 组织管理员移除私有频道订阅者不要求自身具有内容访问；用户组递归仍对缺失、停用和循环失败关闭。
- 创建频道允许“私有 + 对后续订阅者共享历史”，拒绝“私有 + Web 公开”以及公开频道关闭历史共享。
- 所有非幂等请求只有一次尝试；HTTP 2xx 若报告 unsupported ignored parameters 仍按失败处理。

## Rejected approaches

- 拒绝 1:1 复制官方旧版灰色视觉。最终只复刻结构、功能和权限语义，继续使用 RelayCove token、留白、圆角和浅边框。
- 拒绝把 Personal、Subscribers、Permissions 做成可点击占位标签；四页必须有真实数据和写入边界。
- 拒绝用 `CanAdminister` 覆盖所有权限；个人设置和成员添加/移除采用各自 capability。
- 拒绝把匿名 group-setting 静默替换成命名组，也拒绝在没有实际差异时发送高级设置 PATCH。
- 修正了 MAUI 不支持的 `CheckBox.Content`、WPF 风格 Picker 绑定、不可达创建按钮、缺失取消归档入口、窄屏功能降级和旧分页响应回填等中间实现。

## Visual Studio feedback and visual correction

- 首次 Visual Studio 人工检查否决了候选外观：设置页虽然功能可达，但表现为控件直接铺在大面积空白容器中，标题动作、分页和表单控件层级接近，General、Personal、Subscribers 与 Permissions 缺少可扫读的分组，窄屏频道目录还丢失了频道身份信息。
- 最终视觉修正保留 RelayCove 的现代极简 token，不复制 Zulip 的旧版灰色皮肤。覆盖页改为居中限宽壳体，桌面使用目录/分隔线/详情结构；右侧内容限制阅读宽度，频道身份、紧凑动作、分页和设置分区形成稳定层级。
- 桌面与窄屏现在共用同一个富频道行模板，均显示订阅状态、隐私/颜色、名称、说明和可用统计；三种目录模式与四个分页都有明确选中态。
- General 按“关于频道 / 组织 / 集成与文件夹”，Personal 按“频道外观 / 通知 / 整理”，Subscribers 按“已订阅者 / 添加订阅者”，Permissions 按“频道可见性 / 订阅与发言 / 话题与管理 / 保留策略”分区。成员列表具有计数和空态，权限值允许换行。
- 视觉重构期间曾误删若干已接通入口；独立只读回归发现后，订阅/退出、文件夹保存/取消/新建、邮箱复制、七项个人设置、成员选择/移除、四项高级布尔设置、命名组当前值预选以及子弹层外点/焦点均已恢复，并增加静态/VM 回归防止再次发生。
- 用户第二次检查仍否决了修正候选，指出整体观感依旧粗糙且部分页面功能不完整。复核确认手动刷新、移出文件夹和订阅者/候选用户分区确实不可达或语义错误；同时默认 MAUI Picker、CheckBox、Button 与局部扁平按钮混用，造成控件比例、边框、状态和动作层级不统一。
- 最终重做统一采用频道设置专用 Picker、CheckBox、工具按钮、动作行、分页和列表行样式。详情标题动作允许换行，分页可横向滚动；个人通知改为“名称 / 当前状态 / 操作”的紧凑行，订阅者与候选用户使用独立列表和空态，权限与成员列表使用确定高度避免嵌套测量消失。
- 功能补齐包括桌面和窄屏手动刷新、当前文件夹标签与显式移出、创建/编辑错误原位显示、订阅者与候选用户分区，以及私有频道与默认频道互斥。设置页目录 `+` 明确创建频道，频道导航行 `+` 仍只创建所点频道的话题草稿。
- 用户第三次检查明确指出频道颜色缺少直接预览/色盘，并确认订阅者管理没有真正可用。根因是颜色仍为裸 Hex 输入，候选用户又错误依赖 `_session.State.Users` 的不完整聊天快照。最终修正增加本地实时预览、键盘可达色盘、严格 Hex 校验和显式保存；订阅者页改为同代读取权威成员 ID 与 `GET /users` 目录，坏数据和旧响应均失败关闭。
- 用户第四次检查指出“移出”仍不可用，并要求个人频道外观参考官方。最终定位到设置快照把所有频道硬编码为未订阅，私有频道因而被误判为没有内容访问并关闭移除权限；修正后按 `/users/me/subscriptions` 权威合并订阅状态，成功移除捕获频道/成员 ID、只发送一次请求并刷新，失败保持确认层并原位显示错误。个人外观改为官方式紧凑入口、频道预览和精确 24 色色盘，但保留 RelayCove 的“显式确认才写入”安全边界。
- 用户复验时“移除”已经呈现为启用状态，但点击仍未进入确认层。视图原先同时依赖 `Clicked` 记录焦点和 DataTemplate 内跨层 `Command` 绑定执行动作，真实窗口中后者静默未触发；修正为唯一的 `Clicked` 路径，由 code-behind 从按钮行上下文取得成员、调用同一个 VM command 的 `CanExecute/Execute`，并继续保留取消或成功后的焦点返回。该修正已通过编译、App 回归和独立只读复核，等待再次人工确认。
- 用户随后指出颜色修改仍错误：居中遮罩弹窗、被裁切色盘和常驻 Hex 输入都不符合官方交互。最终改为由“修改颜色”按钮坐标驱动的锚定弹层，透明外点层不再压暗设置页，4×6 色盘改用完整非虚拟化布局；“自定义颜色”按需展开原生 WinUI `ColorPicker`，支持色谱、色相和 RGB/Hex 输入。弹层限制最大高度并内部滚动，仍保持确认才写、外点/`Escape` 回滚的产品安全边界。

该次人工反馈仅确认旧候选外观不合格；修正后的视觉结果仍等待用户在 Visual Studio 中复验。

## Deterministic evidence

```powershell
dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore -p:UseAppHost=false -p:OutputPath=.verify/app/
# 0 warnings, 0 errors

dotnet test tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj -c Debug --no-restore --nologo
# 126/126

dotnet test tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj -c Debug --no-restore --nologo
# 85/85

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore -p:UseAppHost=false -p:BaseOutputPath=.verify/app-tests/
# 190/190
```

所有协议测试使用 fake HTTP，App 测试使用 fake session。未运行 Fast、Full、Live、打包，未连接生产 Realm，未执行真实频道创建、成员、权限、订阅、归档或其他写入。

首次把 App build 与 App tests 并行指向同一默认输出目录时，测试侧因生成目录竞争报告临时 `CS5001`/`InitializeComponent` 错误；后续用户正在运行的 `RelayCove.App.exe` 又锁定了默认 apphost。最终验证使用 `UseAppHost=false` 和独立 `.verify` 输出目录串行执行，没有终止或操作用户窗口；App build 与完整 App tests 均通过。上述并发/文件锁不作为代码失败证据。

协议/Core 与 App/UI 的独立只读复核在修正上述问题后没有剩余 P0/P1。

## Visual Studio short check

1. 从不同频道的三点菜单打开设置，确认当前聊天不被切换，设置目标始终是所点频道。
2. 宽窗口检查双栏、三种目录模式、搜索/归档/排序、频道选中态和四个分页；收窄到 720 DIP 以下，确认同样能力仍可达并可返回目录。
3. 在 Personal 确认页面只显示紧凑的当前色块与“修改颜色”；打开颜色子层，检查频道标题实时预览、4×6 色盘、自定义合法/非法 Hex，以及外点/`Escape` 取消后恢复原色并返回焦点；不要点击顶部保存。
4. 在私有且当前已订阅的频道打开 Subscribers，确认有权限时成员行“移除”可用并能打开带姓名的确认；只取消，不执行真实移除。再搜索姓名/邮箱，确认已订阅者不随候选搜索消失、停用用户不进入候选、勾选后添加按钮才启用。
5. 打开创建频道、成员移除和归档确认后用外点与 `Escape`，确认只关闭最顶层；关闭成员确认后焦点回原“移除”按钮，最终关闭设置页后焦点回频道更多按钮。

Manual result: first and second visual candidates rejected as too rough/incomplete; fully reworked candidate pending user Visual Studio confirmation.
