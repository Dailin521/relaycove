# 阶段 17：管理员入口统一到网页后台

## 任务定义

- **任务名称：** 阶段 17 管理员入口统一到网页后台
- **状态：** `已完成`
- **目标版本：** `1.0.0-rc.22`
- **基准提交：** `c6a35a71e53589a7a401320086ecb1576b345542`
- **完成提交：** `bc3475fcacc166dd6ee3ab7a8a0153a14fed0ddc`
- **工作分支：** `agent/stage-17-web-admin-only`
- **相关方案章节：** 7.2–7.3、阶段 11、阶段 15、阶段 16

### 目标

Windows 客户端只保留聊天和个人使用功能，不再显示全局管理员控制面。账号、频道、附件上限等全局管理统一从服务器网页后台完成，降低客户端复杂度并避免两个管理入口行为不一致。

### 已知事实

- `已验证`：网页后台 `https://hklight.2000521.xyz/relaycove/admin/` 已部署，未登录访问返回 302 到登录页。
- `已验证`：Windows 客户端仍包含管理员按钮、完整管理 Overlay、`ClientAdminCoordinator` 及对应测试。
- `已验证`：普通用户频道创建和私有频道成员管理属于聊天功能，不依赖全局管理员 Overlay，必须保留。
- `已验证`：`ClientAdminCoordinator` 同时承载普通聊天的频道创建、用户目录、参与者查询和私有成员管理；因此保留该协调器的实例与聊天复用方法，仅移除全局管理 Overlay 专用 UI 与调用链。
- `已验证`：`main` 与 `origin/main` 一致，线上内部更新通道为 rc.21。

### 假设

- `假设`：rc.22 仅做管理员入口收敛与必要清理，不追加新的聊天功能。

### 范围

- 必须实现：
  - 移除 Windows 客户端全局“管理员”按钮和管理员 Overlay。
  - 停止创建和调用仅服务全局管理员 Overlay 的管理入口链路；保留为普通聊天频道/成员操作复用的协调能力。
  - 删除仅服务于该 Overlay 的死代码和对应失效测试。
  - 保留普通频道创建、私有频道成员管理、管理员身份登录和全部服务器管理 API。
  - 管理员用户在客户端仍可正常聊天；管理操作统一使用网页后台。
- 允许修改：
  - `src/RelayCove.Client/`
  - `tests/RelayCove.Client.Tests/`
  - `docs/ai/`、工程方案中对应说明
- 明确不做：
  - 不删除服务器管理 API、网页后台或数据库字段。
  - 不重做聊天界面，不修改认证、同步、消息或附件协议。
  - 不新增依赖或迁移。

### 验收标准

- [ ] Windows 客户端不再显示全局管理员入口或 Overlay。
- [ ] 管理员账号与普通账号均能正常登录、聊天和使用频道功能。
- [ ] 网页后台继续可登录并完成既有管理操作。
- [ ] 客户端不存在仅由已删除 Overlay 引用的生产死代码。
- [ ] Release 构建、相关定向测试和 Windows GUI 冒烟通过。
- [ ] 生成并验证 rc.22 自包含客户端包；通过后再更新内部通道。

### 验证命令

```powershell
dotnet build RelayCove.sln -c Release --no-restore
dotnet test tests/RelayCove.Client.Tests/RelayCove.Client.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Admin|FullyQualifiedName~ClientAccountComposition|FullyQualifiedName~ClientAccountShell"
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.22
pwsh ./scripts/verify-client-release.ps1 -Version 1.0.0-rc.22
```

### 停止并询问

- 若移除客户端管理员协调器会影响普通频道成员管理，则只移除全局管理 UI，不扩大到聊天管理能力。
- 若发现网页后台缺少 Windows Overlay 已有的必要管理能力，先补齐网页后台再删除对应客户端入口。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
阅读 AGENTS.md、相关工程方案章节、docs/ai/STATUS.md 和本任务文件。
只完成管理员入口收敛，不改聊天协议或服务端数据语义。
优先删除可证明无引用的客户端 UI 与协调代码；保留普通频道成员管理。
只运行相关定向验证和一次 Release 构建，打包前再做发布校验。
完成后更新本文件与 STATUS.md，并按 owner 已授权流程提交、推送、合并和发布。
```

## 任务结果

- `已验证`：已移除 Windows 客户端全局“管理员”按钮、完整 `AdminOverlay` 及其 XAML code-behind 专用事件和状态逻辑。
- `已验证`：保留聊天、普通用户频道创建、用户目录、参与者显示和私有频道成员管理链路。
- `已验证`：Release 解决方案构建 0 警告、0 错误；`ClientAdminCoordinatorTests|ClientAccountShellCoordinatorTests` 定向测试 92/92 通过；`git diff --check` 通过。
- `已验证`：静态检查未发现 `OpenAdminButton`、`AdminOverlay` 或其控件/事件残留引用。
- `未验证`：根据 owner 指令禁止使用 Windows 应用控制能力，本轮不执行 UI Automation 冒烟；发布包的编译启动校验仍由发布脚本完成。
- `已验证`：rc.22 自包含 ZIP 已从干净 `bc3475f` 生成并通过发布校验；大小 `165,840,990` 字节，SHA-256 为 `61480ccc448ca83f6b2305f27917d99a6cb026581b109daa1bdfaf3a4cb6edc3`。
- `已验证`：VPS 端上传后重算 SHA-256 与本地一致，先原子切换 ZIP、最后原子切换 manifest；公网清单已为 rc.22，Range GET 返回 `206` 和精确总长度，网页后台未登录访问仍 `302` 跳转登录页。
