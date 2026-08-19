# RelayCove MAUI 原生 UI 快速预览手册

这份手册记录 Stage 22M 在 Visual Studio 2026、.NET 10 和 Windows 多显示器环境中的实际开发经验。目标是缩短“修改—观察—校正”循环，同时保留原生 XAML、编译绑定、真实 DPI 和正式验收边界。

## 1. 不可跨越的边界

- 快速预览只允许 `Debug` + `RELAYCOVE_NATIVE_UI_PREVIEW=1`。该组合注入 `#if DEBUG` 的内存 `IClientSession`，不会登录、联网或向 Realm 写入。
- 普通 Debug、Release、安装包和真实 Realm 仍使用生产 `ClientSession`。预览开关不得进入 Release 行为。
- 快速预览不得通过 WebView、代理、本地 HTTP 服务或跳过 XAML 编译实现。MAUI 继续原生复刻正式 Web 的 UI 和交互。
- 预览截图只证明原生窗口的视觉与局部交互，不证明 Stage 21 Live、人工登录、真实消息写入、安装包或干净 VM。

## 2. 当前工具链基线

| 项目 | 当前值 |
|---|---|
| Visual Studio | Visual Studio 2026 当前安装版本 |
| .NET SDK | `10.0.400` |
| `global.json` | `10.0.400`，`rollForward: latestPatch` |
| workload set | `10.0.400.1` |
| `maui-windows` manifest | `10.0.20/10.0.100` |
| `Microsoft.Maui.Controls` | `10.0.20` |
| Windows TFM | `net10.0-windows10.0.19041.0` |
| Windows RID | `win-x64`；发布脚本也显式覆盖为同一 RID |

Visual Studio 或 SDK 更新后先执行只读检查：

```powershell
dotnet --list-sdks
dotnet --version
dotnet workload list
```

检查结果必须与 `global.json`、`RelayCove.App.csproj` 和已安装 workload 对齐。SDK 不存在时应显式升级或安装，不要通过删除 `global.json`、放宽到任意 SDK、移除 MAUI 版本或取消 RID 来掩盖问题。

`restore`、workload 安装和浏览器安装属于显式的一次性依赖准备；普通 Fast/Full 仍使用仓库既有的 `--no-restore` 离线验证路径。

## 3. Visual Studio 正确启动方式

1. 打开当前工作树中的 `RelayCove.sln`，不要打开另一个 worktree 的同名解决方案。
2. 将 `RelayCove.App` 设为启动项目。
3. 选择 `Debug`、`Windows Machine`。
4. 使用项目原生的 `Windows Machine` profile；它只设置 `RELAYCOVE_NATIVE_UI_PREVIEW=1`。
5. 首次编译成功后保持调试会话运行，在同一窗口完成一组相关视觉调整。

不要创建带固定 `executablePath` 的自定义预览 profile。MAUI 的 Windows 启动需要 Visual Studio/MSBuild 处理 TFM 和 RID；自定义路径容易寻找不存在的无 RID 输出：

```text
bin\Debug\net10.0-windows10.0.19041.0\RelayCove.App.exe
```

当前 Windows 输出位于 `win-x64` 子目录；自包含发布也由验证脚本显式传入 `RuntimeIdentifierOverride=win-x64`。路径应由项目配置生成，不应复制进 launch profile。SDK、MAUI 或 RID 变化后需显式执行一次 `dotnet restore RelayCove.sln -r win-x64 -p:Configuration=Release`，同时准备 Release ReadyToRun 运行包；随后日常 Fast/Full 继续 `--no-restore`。

## 4. 两种编辑循环

### 4.1 在 Visual Studio 内编辑 XAML

- 保持 Debugger 附加，使用 MAUI XAML Hot Reload、Live Visual Tree 和属性检查器。
- 在 Visual Studio 中保存 XAML/Style/ResourceDictionary 后，直接观察运行中的窗口。
- 同一组件的小改动不重复 build、启动和截图；到一个可判断的组件状态再保留一张检查图。

### 4.2 Codex 或其他外部工具写入文件

Visual Studio 不保证接管外部 `apply_patch` 的 XAML 保存事件。当前项目实测 `dotnet watch --list` 也没有监听原始 `*.xaml`，只有相关 `*.xaml.cs`，因此不能把 `dotnet watch` 当作可靠的 MAUI XAML 自动刷新器。

稳定循环是：

1. 一次完成一个局部目标的 XAML、Token 和 Style 修改。
2. 只增量编译 App，不编译整个解决方案：

   ```powershell
   dotnet build src/RelayCove.App/RelayCove.App.csproj -c Debug --no-restore
   ```

3. 编译成功后只重启一次预览进程；编译失败时保留旧窗口便于对照。
4. 新预览会再次执行 Debug-only 副屏定位，自动回到非主显示器。

C# 类型、Behavior、DI、项目文件、资源项、编译绑定源类型或窗口启动逻辑发生变化时，必须走增量编译和重启，不能期待 Hot Reload。

如后续制作一键 watcher，必须满足：只监听 App 源 XAML/资源、排除 `bin/obj`、250–300 ms 去抖、编译成功后才停止它自己启动的进程、不得按进程名误杀其他 worktree，并让子进程继承 `RELAYCOVE_NATIVE_UI_PREVIEW=1`。它是“自动增量重启器”，不是 Hot Reload，也不能替代测试。

### 4.3 无鼠标场景预览

不要通过移动用户鼠标、发送系统点击或抢占键盘焦点来打开预览弹层。仓库提供 Debug-only 场景入口，启动时直接把 ViewModel 投影到指定 UI 状态：

```powershell
pwsh ./scripts/start-maui-preview.ps1 -Scene shell -Theme light -Width 1440 -Height 900
pwsh ./scripts/start-maui-preview.ps1 -Scene composer-emoji -Theme light -Width 1440 -Height 900 -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene message-menu -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene reaction-picker -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene account-menu -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene settings -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene details -Width 1024 -Height 768 -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene narrow-list -Width 640 -Height 900 -NoBuild
pwsh ./scripts/start-maui-preview.ps1 -Scene narrow-chat -Width 640 -Height 900 -NoBuild
```

固定场景为 `shell`、`details`、`message-menu`、`composer-emoji`、`reaction-picker`、`account-menu`、`settings`、`narrow-list`、`narrow-chat`；主题为 `light`、`dark` 或 `system`。首次修改后省略 `-NoBuild`，脚本会先把 App-only Debug build 输出到忽略的 `artifacts/maui/preview-builds/<timestamp>/`；只有构建成功才替换它自己记录的旧预览进程。每个预览 EXE 必须以自身输出目录作为工作目录，否则 WinUI 可能因找不到本地运行时资源而在创建 HWND 前退出。仅切换场景时使用 `-NoBuild`，通常一秒内即可重新打开。双击根目录的 `start-maui-preview.cmd` 会打开默认 `shell` 场景。

场景、主题和窗口大小分别由 `RELAYCOVE_NATIVE_UI_PREVIEW_SCENE`、`RELAYCOVE_NATIVE_UI_PREVIEW_THEME`、`RELAYCOVE_NATIVE_UI_PREVIEW_WIDTH`、`RELAYCOVE_NATIVE_UI_PREVIEW_HEIGHT` 选择，并且只有 `Debug`、`RELAYCOVE_NATIVE_UI_PREVIEW=1` 和内存 `NativeShellPreviewSession` 同时成立时才应用。正式 Debug、Release 与真实 `ClientSession` 不读取这些场景。

仓库内的捕获脚本只接受启动器记录的 PID，并核对进程真实路径属于当前 worktree；它先把 HWND 放到副屏、等待 per-monitor DPI/预览定位计时器稳定，再用 `DwmGetWindowAttribute` 和 `PrintWindow(PW_RENDERFULLCONTENT)` 抓取窗口：

```powershell
pwsh ./scripts/capture-maui-preview.ps1 `
  -DipWidth 1440 -DipHeight 900 `
  -OutputPath artifacts/maui/screenshots/parity-polish/shell-1440-light.png
```

该链路不会移动鼠标、发送点击或键盘输入，也不会按进程名误抓另一个 worktree。截图进入被忽略的 `artifacts/maui/screenshots/parity-polish/`；正式交付在 STATUS/task 记录路径、目标 DIP、实际像素和 SHA-256。

## 5. 推荐验证节奏

| 时机 | 动作 |
|---|---|
| 连续视觉微调 | 观察现有窗口，不截图、不跑全套测试 |
| 一个组件或响应状态完成 | App-only build；必要时跑相关 App/ViewModel 测试；保留一张检查图 |
| 一个完整 Slice 完成 | `pwsh ./scripts/verify.ps1 -Mode Fast` |
| 交付提交前 | `pwsh ./scripts/verify.ps1 -Mode Full`、正式截图和独立只读复核 |
| Live/真实写入 | 仅在独立凭据、白名单和明确写授权齐全时运行 |

最窄 App 测试命令：

```powershell
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-build --no-restore
```

不要为了每一个像素修改重复 Full、发布、安装包或全视口截图。也不要以一次 Hot Reload 的可见结果替代编译、XamlC 或 Release 验证。

## 6. 副屏与 DPI 经验

- Debug 离线预览每次启动只选择一个非主显示器；未连接副屏时安全保留在主屏。生产启动不强制移动窗口。
- MAUI 的目标大小使用 DIP；`AppWindow.MoveAndResize` 和 DWM 捕获边界使用物理像素。换算公式为 `physical = DIP × scale / 100`。
- 150% 副屏上，1440×900 DIP 应对应约 2160×1350 物理像素。
- 窗口刚移动到另一个 DPI 的显示器时，`GetDpiForWindow` 可能仍返回原显示器 DPI。副屏初始化使用目标 monitor 的 `GetScaleFactorForMonitor`，再延迟一次 `MoveAndResize`。
- 不要对 `DisplayArea.FindAll()` 的 WinRT 投影直接使用 LINQ `FirstOrDefault`；本项目实际遇到过 `InvalidCastException`。当前 Windows adapter 使用 Win32 monitor 枚举。
- Hot Reload 不会重新执行 C# 启动定位逻辑；只有新启动或显式重启才会重新放到副屏。
- 截图尺寸以 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 为准；PowerShell 普通 `GetWindowRect` 可能受调用进程 DPI 虚拟化影响。正式窗口捕获使用 `PrintWindow(PW_RENDERFULLCONTENT)`，避免把桌面或其他窗口录入证据。

### 6.1 先判断整体层级，再校正局部尺寸

- 视觉复核先看大区域的“地面—表面—浮层”关系：导航/会话区使用 `Ground`，聊天画布必须以 `Surface` 从标题下方连续铺到底，Composer 使用 `SurfaceSoft` 并靠外边距形成悬浮卡片。不要因为 Composer 本身颜色正确，就忽略它周围暴露了错误的根背景。
- Web 的百分比约束要转译成原生布局语义。例如消息行在标准/紧凑布局中是聊天内容宽度的 76%，窄屏是 90%，再受 690 DIP 上限控制；不能只复制一个 690 数字，否则 1440 看似正常、1024 会明显铺得过宽。
- 列表重复会破坏整体节奏。频道只有一个可靠话题时直接进入该会话并隐藏重复“话题”区；存在多个话题时才显示原生选择区。保留真实能力的同时，避免为了数据结构把同一会话展示两次。
- 先用整窗 1440/1024/640 截图判断区域比例，再截局部验证边缘与焦点。局部裁剪不能用来推断整窗控件已越界；应以启动器记录的目标 DIP、DWM 实际像素和完整窗口图共同判断。
- 不强求把 CSS 阴影逐值搬到 MAUI。WinUI 对 MAUI `Border.Shadow` 的扩散范围与 CSS 不等价，本项目实测会把局部 popover 阴影扩散成大面积灰层。出现这类平台失真时优先保留清晰描边、层级和安全边距，而不是继续堆半径/透明度。
- 表情盘等浮层要同时检查内容测量和窗口边界：完整 6 列、右边框、底部最后一行均可见，且 `PopoverAnchorBehavior` 在根窗口保留边缘余量。不要依赖 WinUI `CollectionView` 的默认选择描边：它会在六列取整后的单元边界丢失右侧 1 个物理像素，而且仅给模板根增加 Padding 无法修复。当前实现关闭原生选择视觉，保留 ViewModel 键盘索引，并用 34×34 DIP、单元内居中且覆盖在按钮上层的 MAUI `Border` 绘制完整状态框；按钮本身为 30×30 DIP。在目标 150% DPI 下使用 2 DIP 描边会得到整数 3 物理像素，避免 1.5 DIP 描边的半像素抗锯齿看起来仍像裁边。验收图必须是无鼠标/键盘输入的固定场景，避免人为点击位置掩盖锚点问题。

### 6.2 消息虚拟列表经验

- Preview 的短文本和固定高度不能证明正式会话滚动正确。真实 raw Markdown、引用、reaction、附件和不同行高会改变 WinUI 虚拟列表 extent；首次加载和 A→B→A 必须在 formal non-preview 客户端复核。
- `KeepScrollOffset` 只定义集合更新时的原生偏移策略，不代表切换会话后应该保留旧会话的绝对偏移。每次会话访问都需要新的 latest-scroll request。
- 不要猜测虚拟列表何时完成最后一次布局。首次实现最后一项并到达底部后立即显示；只要用户没有主动向上滚，后续布局变化都继续把视口锚定到最新消息。用户滚轮、拖动滚动条或使用滚动键时立即解除底部锚定。
- 不要恢复全局“只要之前靠近底部，任何 LayoutUpdated 都滚到底”的补丁。它会把发送、图片测量、Composer 布局变化和分页全部变成重复 `ChangeView`，表现为消息闪屏。
- 自动化记录至少包含会话名、`VerticalScrollPercent`、首/末可见消息和最终截图。只看最终一帧无法发现 70%→100% 的中间跳动；只看百分比也不能证明最后一条消息确实可见。

### 6.3 Windows Composer 光标经验

- WinUI 原生输入控件的 caret blink rate 与 caret timeout 是不同设置。同位置 `Select` 不会可靠重启 timeout，不能把“最初闪了几次”当成持续光标验收。
- 不要在原生 caret 上绘制背景色遮罩。插入点可能紧贴 CJK 字形，遮罩会留下白条、裁字或与系统 caret 形成双光标。
- 原生 `RichEditBox` 能满足持续光标时，保留它自己的 caret，让 Windows 负责位置、字形高度与闪烁。不要叠加应用光标；若原生行为异常，先核对焦点丢失、控件重建和选择回写，而不是猜测文字坐标。
- 自动化至少截图文本开头、中间字符边界、末尾、换行，以及系统 timeout 之后一组“显示/隐藏”帧。副屏 DPI 下必须使用目标 HWND 的 `PrintWindow`；通用屏幕区域截图可能因虚拟桌面坐标缩放截到主屏。

## 7. 常见故障

| 现象 | 主要原因 | 处理 |
|---|---|---|
| “调试可执行文件不存在” | 自定义 profile 绕过 MAUI RID，或当前配置尚未 build | 恢复 `Windows Machine`，确认 `win-x64` 输出并增量 build |
| VS 更新后无法解析 SDK | `global.json` 指向已卸载 SDK | 检查已安装 SDK/workload，显式对齐版本 |
| 外部改了 XAML，窗口没有变化 | VS Hot Reload 未接管外部保存 | 成批修改后 App-only build，再重启一次 |
| `dotnet watch` 没有刷新 XAML | 当前 watch 输入不含原始 XAML | 不作为默认方案；使用 VS Hot Reload 或增量重启 |
| 副屏窗口尺寸过大/过小 | 混用了 DIP、物理像素或原显示器 DPI | 读取目标 monitor scale，再换算目标像素 |
| 副屏枚举时 `InvalidCastException` | WinRT `DisplayArea` 集合经 LINQ 投影失败 | 使用 Windows adapter 的 Win32 monitor 枚举 |
| 编译失败后预览也消失 | 自动化先结束旧进程再 build | 顺序改为 build 成功后才替换旧预览 |
| Debug build 成功但窗口在 HWND 创建前崩溃 | 从仓库根目录启动独立输出的 WinUI EXE | 用启动器固定 `WorkingDirectory` 为该 EXE 所在构建目录 |
| 连续场景截图尺寸偶尔沿用主屏缩放 | HWND 尚未完成副屏 DPI 迁移或与启动定位计时器竞态 | 先无激活移动到副屏，等待启动计时器完成，再读取 DPI 和最终调整 DWM 边界 |
| 缓存会话重新进入后随机停在中段或约 97.5% | 把上一次激活确认永久去重，或者只定位一次后任由后续行高变化推离底部 | 每次访问重置激活身份；首次到底立即显示，并在用户未主动滚动时持续锚定最新消息 |
| 发送后消息区反复闪动 | 用全局 LayoutUpdated 保底或让 RealtimeFollow 进入激活重试循环 | 分离激活、实时跟随和分页锚点；实时追加保持列表可见且不运行激活稳定循环 |
| Composer 光标约 5 秒后停止，或自绘后出现白条/切字/位置异常 | 焦点/控件生命周期或选择回写有问题，或叠加/遮盖系统 caret | 保留原生 caret；先复现并核对焦点、控件重建和选择回写，逐一验证首次空点击、CJK 开头/中间/末尾、切换/发送后聚焦与 timeout 后帧 |

## 8. 交付记录

STATUS 和活跃任务必须分别记录：工具链版本、启动 profile、是否使用离线 preview、显示器/DPI、实际执行的窄测试/Fast/Full、截图路径以及仍未验证的 Live/真实 Realm/VM 门禁。不得用“窗口能打开”概括这些不同证据。
