# RelayCove rc.24 UI 合集

本目录保存 rc.24 客户端界面的可提交基线，供后续版本进行视觉对照。

## 快照

| 文件 | 窗口尺寸 | 展示状态 |
| --- | --- | --- |
| `main-window-outer-1280x720.png` | 1280 × 720 | 窄窗口；回复、10 个附件和成员抽屉自动收起。 |
| `main-window-outer-1600x900.png` | 1600 × 900 | 三栏聊天、消息流、输入区和右侧成员抽屉。 |
| `main-window-outer-1920x1080.png` | 1920 × 1080 | 宽屏三栏布局、成员管理操作和完整内容区域。 |

## 来源与边界

- 已验证：快照由客户端 WPF `RenderTargetBitmap` 渲染测试生成，并已进行图像复核。
- 已验证：rc.24 客户端 UI 的代码基线为提交 `9730a14ea736c83355ec7d8af0a78c5e024c8562`；本合集随后的验证记录提交一并保留在仓库历史中。
- 未验证：这些快照不代表真实双账号、托盘、断网、旧 Toast 或安装态更新的桌面人工验证。

相关设计约束见 [`../../ui-design-guidelines.md`](../../ui-design-guidelines.md)，快照测试见 `tests/RelayCove.Client.Tests/Desktop/ClientUiSnapshotTests.cs`。
