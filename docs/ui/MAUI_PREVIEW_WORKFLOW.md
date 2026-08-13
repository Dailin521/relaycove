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

## 8. 交付记录

STATUS 和活跃任务必须分别记录：工具链版本、启动 profile、是否使用离线 preview、显示器/DPI、实际执行的窄测试/Fast/Full、截图路径以及仍未验证的 Live/真实 Realm/VM 门禁。不得用“窗口能打开”概括这些不同证据。
