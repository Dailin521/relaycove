# 阶段 8 安全链接识别与显式打开

## 任务定义

- **任务名称：** 阶段 8 Text 消息 HTTP(S) 链接识别、当前快照授权与外部浏览器打开
- **状态：** `已完成`
- **基准提交：** `c55cebb4a31a15c6cd38580afdab6c7bfaa468f8`
- **工作分支：** `agent/stage-8-safe-links`
- **相关方案章节：** 9.2–9.4、18.4、阶段 8；`DEC-034`、`DEC-037`

### 目标

从当前 Ready 消息的实际展示正文中确定性识别有限数量的绝对 HTTP(S) 链接，作为显式按钮展示；只有用户点击且链接仍属于当前账户/会话不可变快照时，才用 Windows shell 的 URL 关联打开。任何自定义协议、URI user-info、陈旧行、参数化命令或后台自动打开都 fail-closed，链接/正文不进入日志或 `ToString()`。

### 已知事实

- `已验证`：绿色集成头 `c55cebb` 已包含 766/766 的复制/日期切片；当前分支建立时工作树干净。
- `已验证`：消息 presentation 已统一生成确认/pending 行并由当前 Ready snapshot 驱动；MainWindow 的 Copy/Reply handler 均可复用“事件值仍属于 displayed snapshot”的 fail-closed 门。
- `已验证`：Client 当前没有 Hyperlink、`ProcessStartInfo` 或外部 URI 打开实现，也没有可复用命令行/浏览器启动器；因此不会与旧行为兼容冲突。
- `已验证`：Text 上限为 4000 Unicode scalar，仍需限制单消息识别数量与单 URL 长度，避免恶意正文生成大量 WPF 控件或极长 shell 请求。
- `已验证`：Claude #65 MCP 只读安全 challenge 因本机认证源优先级失败，无 job、模型、workspace、费用或结论；Codex 继续负责威胁建模、实现和本机验证。

### 假设

- `假设`：第一版最多展示每条消息前 8 个按首次出现去重的链接；单候选最多 2048 字符。
- `假设`：只接受 `http`/`https`、绝对 URI、非空 host、空 user-info；localhost、IP、内网和 query/fragment 可在显式用户点击后交给系统浏览器，客户端不替用户做内容信誉判断。
- `假设`：常见中英文句末标点不属于链接；成对圆/方/花括号只移除未匹配的尾部闭括号，保留 URL 内平衡括号。

### 范围

- 必须实现：
  - 无 regex、线性扫描的 Text link parser；大小写不敏感识别 `http://`/`https://`，截断空白/控制字符并剥离明确尾部标点。
  - presentation 暴露只读、脱敏 link 列表；WPF 为识别结果提供可换行的显式链接按钮，不改变正文或 Copy 内容。
  - 点击前核对 link 值仍属于当前 Ready snapshot；launch policy 再验证 scheme/host/user-info/长度，构造 `UseShellExecute=true`、无 Arguments/Verb/WorkingDirectory 的 `ProcessStartInfo`。
  - 关联不存在/启动失败显示脱敏可恢复状态；不得记录或格式化 URL、正文、身份、ID 或路径。
  - 覆盖大小写、多个/重复、中文相邻、标点/括号、上限、畸形 URI、user-info、自定义 scheme、当前/陈旧/撤权快照、ProcessStartInfo 字段与启动失败。
- 允许修改：
  - Client Accounts presentation/link policy、`MainWindow.xaml(.cs)` 与对应 Client 测试；必要的 `docs/ai/` 记录。
- 明确不做：
  - 自动打开、网页预览/抓取、信誉/反钓鱼服务、重定向跟随、内网/公网分类、富文本 inline Hyperlink、`mailto/file/ftp`、复制链接、`@用户`、新消息分割线、新依赖或协议/schema 变化。

### 验收标准

- [x] parser 只产出最多 8 个、<=2048 字符、无 user-info 的绝对 HTTP(S) 链接；畸形/自定义协议与明确尾标点被拒绝或剥离，正文保持原样。
- [x] 只有当前 Ready snapshot 中仍授权的 link 可触发 launcher；陈旧、篡改、非 Ready/撤权行不启动进程。
- [x] launcher 只把规范绝对 URI 放入 `ProcessStartInfo.FileName`，`UseShellExecute=true` 且不使用 Arguments/Verb/WorkingDirectory；已知 shell 失败如实反馈且不泄露 URL。
- [x] Fast/Full、parser/policy/launcher/presenter 定向与重复、model drift、八项目漏洞审计、空白检查和真实 Windows WPF smoke 通过；自动化不实际打开浏览器。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ClientMessageLink|FullyQualifiedName~ClientExternalLink|FullyQualifiedName~ClientMessageListPresenter"
pwsh ./scripts/verify.ps1 -Mode Full
dotnet ef migrations has-pending-model-changes --project src/RelayCove.Server/RelayCove.Server.csproj --startup-project src/RelayCove.Server/RelayCove.Server.csproj --context RelayCoveDbContext --configuration Release --no-build
dotnet list RelayCove.sln package --vulnerable --include-transitive
git diff --check
```

### 停止并询问

- 必须引入浏览器/富文本依赖、改变消息正文/Copy 语义、允许非 HTTP(S) scheme、后台联网或读取 VPS 配置。
- 无法用参数化 shell launch 避免命令行拼接，或需要保存/上传用户点击历史。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、工程方案 9.2–9.4/18.4/阶段 8、docs/ai/STATUS.md 和本任务。
只实现有界 HTTP(S) 识别与当前快照显式打开；不实现预览、信誉、@、未读分割或外部协议。
parser 保持线性且不改正文；launcher 必须二次校验并禁止 Arguments/Verb/WorkingDirectory。
所有 URL/正文/身份/ID 保持日志和 ToString 脱敏；自动化使用注入启动器，不实际开浏览器。
```

## 任务结果

### 修改摘要

- 新增无 regex 的有界链接 parser：只接受绝对 HTTP(S)、非空 host、空 user-info，限制每条消息最多 8 个去重链接和每项 2048 字符；剥离明确尾标点及未匹配闭括号，同时保留正文和 Copy 原值。
- presentation 暴露脱敏的不可变链接值，WPF 在正文下方渲染显式按钮；点击前重新核对值仍属于当前 `Ready` 消息快照。
- 新增二次校验 launcher，仅把规范 URI 放入 `ProcessStartInfo.FileName` 并启用 Windows shell association；已知失败只返回脱敏状态，自动化通过注入启动动作保证不实际打开浏览器。
- Codex 固定差异自审发现初版尾括号平衡会对每个候选重复扫描并形成二次复杂度，已重构为候选内单次计数后复验。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Fast` | 782/782：Shared 35、Server 175、Client 571、Updater 1。 |
| `已验证` | `pwsh ./scripts/verify.ps1 -Mode Full` | 同一代码树提交前及固定代码提交 `018df9840fcb35a6a5ee41b21191b129792a2f2f` 各通过一次；Release 构建 0 警告/0 错误、format、782/782 与 `git diff --check` 通过。 |
| `已验证` | parser/policy/launcher/presenter Release 定向重复 | 每轮 19 项，连续 10 轮共 190/190；自动化使用注入动作，未打开真实浏览器。 |
| `已验证` | EF model drift 与依赖审计 | `has-pending-model-changes` 无差异；解决方案 8 个项目含传递依赖均无已知漏洞。 |
| `已验证` | 敏感链接日志检索 | URL/正文进入日志的匹配数为 0；presentation 与状态输出保持脱敏。 |
| `已验证` | 真实 Release WPF smoke | 主进程 PID 27556、非零窗口句柄 91360668、`Responding=True`；第二实例 PID 54112 正常退出、匹配实例保持 1，精确清理后为 0。 |
| `未验证` | 真实外部浏览器打开、VPS/真实登录视觉/双客户端/Narrator | 浏览器自动打开刻意排除；其余保留到 M5 Gate。 |
| `未验证` | Claude #65 独立安全 challenge | MCP 因本机认证源优先级失败，无 job、模型、workspace、费用或结论；不冒充通过，Codex 威胁建模与本机门禁为最终依据。 |

### 文件范围

- 新增：`ClientMessageLinkPresentation`、`ClientMessageLinkParser`、`ClientMessageLinkPolicy`、`ClientExternalLinkLauncher` 及三组对应测试。
- 修改：消息行 presentation/presenter、`MainWindow.xaml(.cs)`、presenter 测试，以及任务/状态/执行/决策记录。
- 删除：无。

### 决策与限制

- 决策：接受 `DEC-038`，冻结有界 HTTP(S) 识别、当前快照授权与参数化 shell 打开边界。
- 已知限制：不做网页预览、信誉判断或非 HTTP(S) 外部协议；真实浏览器不由自动化打开。

### 下一步

- 将完成提交仅快进到 `agent/v1-integration`，随后进入阶段 8 新消息分割线切片。
