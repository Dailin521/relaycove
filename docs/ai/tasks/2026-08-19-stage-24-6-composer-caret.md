# Stage 24.6 — Windows Composer 原生光标（工作日志）

- Status: completed; user-confirmed and pushed in `main@2c85700`
- Starting point: `main@2fc29a2`; finalized commit: `main@2c85700`
- Date: 2026-08-19 CST
- Scope: `RelayCove.App` Windows Composer 与确定性 App 测试；不修改 Web、Core、Data、协议、服务端或部署

## 用户现象

1. 输入框光标在切换会话或点击输入后出现迟缓，数次闪烁后停止。
2. 为了强行维持闪烁曾尝试自绘光标，随后出现双光标、白条、裁字，以及 CJK 字符边界/纵向位置不自然。

## 根因与被否决的方案

- 对 MAUI `TextBox` 重复同位置选择，不是可靠的原生光标生命周期修复。
- 背景色遮罩系统光标会与相邻 CJK 字形共享像素边界，必然可能出现白条和裁字。
- Canvas 自绘光标要求自行猜测 RichEdit 的空文本、字符边界、行框和字体几何；即使局部坐标正确，视觉仍不等同 Windows 原生输入框。

## 最终实现

- 新增 `ComposerEditor` 与 Windows `ComposerEditorHandler`，使用 `RichEditBox` 作为 Windows 原生多行文本控件。
- `RichEditTextDocument.CaretType` 保持 `Normal`：Windows 自己负责光标位置、字体高度、选区状态和闪烁。应用不绘制第二条光标、不运行光标计时器，也不修改系统 caret timeout、注册表或用户设置。
- 保留原生文本引擎处理 IME、选区、撤销、滚动和文本输入；CRLF/selection offset 在 MAUI 字符串与 RichEdit 段落模型间显式转换。
- 保留产品键位：普通 Enter 发送、Ctrl+Enter 换行；IME 组合输入不触发发送。顶部边框拉伸、附件拖放、工具栏和发送按钮不变。

## 验证

### 确定性验证

```powershell
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj `
  -c Debug --no-restore --nologo --verbosity minimal `
  -p:OutputPath=artifacts/local-test/composer-native-caret/
```

- App xUnit：129/129 通过。
- 覆盖 RichEdit CRLF/文档索引映射；Enter/Ctrl+Enter/IME 决策保持既有回归覆盖。

### 真实 Windows 副屏验证

- 启动器：`pwsh scripts/start-maui-preview.ps1 -Scene shell -Theme light -Width 1024 -Height 768`
- 进程：Debug-only `NativeShellPreviewSession`，无网络、无真实 Realm 写入。
- 显示器：`DISPLAY2`，DPI 144 / 150%；目标 1024×768 DIP，实际 1536×1152 像素。
- 使用真实副屏鼠标点击、Windows UI Automation 和 `SendInput`；完成后恢复原鼠标位置与 Visual Studio 前台窗口。

| 场景 | 结果 | 证据 |
|---|---|---|
| 首次点击空输入框 | 系统光标使用正常 Windows 几何并贴合 placeholder 起点 | `native-system-caret/01-first-click-empty.png` |
| “上午好”末尾/中间 | 光标位于原生文本边界，无叠加或裁字 | `02-text-end.png` / `03-caret-middle.png` |
| 选区、Ctrl+Enter | 选区替换正确；多行文本正确 | `04-selection.png` / `05-multiline.png` |
| 切换会话、发送 | 切换后重新点击可输入；普通 Enter 后草稿清空并可继续输入 | `06-after-conversation-switch-empty.png` / `08-after-send-clear.png` |
| 超过 5 秒 | 三次采样为隐藏→显示→隐藏；原生光标仍在闪烁 | `09-blink-after-5s-a.png` / `10-blink-after-5s-b.png` / `11-blink-after-5s-c.png` |

证据目录：`artifacts/maui/screenshots/stage24-6-composer-caret/native-system-caret/`。

## 已知边界与门禁

- 本轮窗口证据来自 Debug-only 离线预览，不替代正式 Realm、人工密码登录、Live、打包或干净 VM 门禁。
- 未运行 Fast/Full/Live，未执行真实消息、已读、附件或其他 Realm 写入。
- 用户已确认本轮 Windows 结果；最终代码、测试与文档已提交并非强制推送至 `main@2c85700`。

## 可复用经验

1. 原生文本控件能满足目标时，光标必须交给系统；不要为了“持续闪烁”先接管绘制。
2. 若光标异常，先检查焦点、控件重建和选择回写；不要用背景遮罩或一层自绘线修补。
3. 对原生输入仍须真实验证首次空点击、CJK 边界、切换会话、发送后焦点、IME 与 timeout 后闪烁。
