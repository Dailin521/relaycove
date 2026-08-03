# 阶段 0：可构建解决方案骨架

## 任务定义

- **任务名称：** 阶段 0 — 创建可构建解决方案和真实验证脚本
- **状态：** 进行中
- **基准提交：** `1dd6eb08c1f839bf651a433e9dc647347ef68469`
- **工作分支：** `agent/stage-0-buildable-scaffold`
- **相关方案章节：** `RelayCove_工程落地方案.md` 第 3、5、6 节与阶段 0；`docs/ai/WORKFLOW.md`

### 目标

建立 .NET 10 的最小可运行工程骨架，使四个源项目和四个镜像测试项目可还原、编译和测试，并用真实 `Fast` / `Full` 脚本统一验证。该切片只证明工程基线，不实现共享协议或业务功能。

### 已知事实

- `已验证`：基准提交没有 `RelayCove.sln`、`src/`、`tests/` 或 `scripts/verify.ps1`。
- `已验证`：本机已安装 .NET SDK `10.0.101`、ASP.NET Core 10.0.1 与 WindowsDesktop 10.0.1；`dotnet new` 提供 `sln`、`wpf`、`web`、`console` 和 `xunit` 的 .NET 10 模板。
- `已验证`：工程方案指定 .NET 10、WPF 客户端、ASP.NET Core 服务端、共享项目和极简 Updater；AGENTS 要求测试镜像源项目。
- `已验证`：`agent/v1-integration` 当前绿色头为 `1dd6eb08c1f839bf651a433e9dc647347ef68469`，其中没有绿色代码提交。

### 假设

- `假设`：第一版开发机和 CI 使用 .NET 10 SDK；`global.json` 固定 `10.0.101` 并允许同一功能带的最新 patch。
- `假设`：阶段 0 使用 ASP.NET Core 共享框架的默认 Microsoft.Extensions.Logging 作为服务端基础日志，不在本任务引入 Serilog 或客户端 Host。

### 范围

- 必须实现：
  - 创建 `RelayCove.sln` 与 `global.json`，目标框架为 .NET 10。
  - 创建 `RelayCove.Client` WPF、`RelayCove.Server` ASP.NET Core、`RelayCove.Shared` 类库、`RelayCove.Updater` 极简控制台项目。
  - 创建四个对应 xUnit 测试项目和正确 ProjectReference；测试只验证程序集基线，不预写业务契约。
  - 用共享构建属性开启 Nullable、隐式 using 和确定性构建；用 `.editorconfig` 固定四空格和文件级命名空间风格。
  - 服务端启动时使用框架内置日志；WPF 只保留可启动最小窗口；Updater 只保留成功退出入口。
  - 新增 `scripts/verify.ps1`：Fast 执行 restore、Debug build、tests；Full 执行 restore、format verify、Release build、tests 和 `git diff --check`。
  - 更新 README、STATUS、V1_EXECUTION 和本任务证据。
- 允许修改：
  - `RelayCove.sln`
  - `global.json`
  - `Directory.Build.props`
  - `.editorconfig`
  - `.gitignore`
  - `README.md`
  - `src/RelayCove.Client/**`
  - `src/RelayCove.Server/**`
  - `src/RelayCove.Shared/**`
  - `src/RelayCove.Updater/**`
  - `tests/RelayCove.Client.Tests/**`
  - `tests/RelayCove.Server.Tests/**`
  - `tests/RelayCove.Shared.Tests/**`
  - `tests/RelayCove.Updater.Tests/**`
  - `scripts/verify.ps1`
  - `docs/ai/STATUS.md`
  - `docs/ai/V1_EXECUTION.md`
  - 本任务文件
- 明确不做：
  - 不定义 Stage 1 DTO、枚举、错误码或协议版本。
  - 不加入 EF Core、SQLite、SignalR Client、CommunityToolkit、Serilog、通知、安装器或部署脚本。
  - 不创建未来业务目录、空服务、占位接口、CI、发布包或 UI 设计。
  - 不修改冻结同步契约和 `DEC-003`。

### 验收标准

- [ ] `RelayCove.sln` 精确包含 8 个项目，引用方向符合职责边界。
- [ ] 所有项目使用 .NET 10；Client/Updater 及其测试使用 Windows TFM，Server/Shared 使用跨平台 TFM。
- [ ] `pwsh ./scripts/verify.ps1 -Mode Fast` 通过 Debug 构建和全部测试。
- [ ] `pwsh ./scripts/verify.ps1 -Mode Full` 通过格式、Release 构建、全部测试和空白检查。
- [ ] 每个测试项目至少发现并通过 1 个真实测试，合计至少 4 个。
- [ ] `-SolutionPath` 指向不存在的文件时脚本非零退出，证明验证不会伪造成功。
- [ ] README 不再声称仓库没有可运行客户端或服务端，并给出准确验证命令。
- [ ] 最终差异只包含文件白名单，工作区干净，无 `bin/obj/TestResults` 进入 Git。

### 验证命令

```powershell
pwsh ./scripts/verify.ps1 -Mode Fast
pwsh ./scripts/verify.ps1 -Mode Full

pwsh ./scripts/verify.ps1 -Mode Fast -SolutionPath ./missing.sln
if ($LASTEXITCODE -eq 0) { throw '负向验证意外成功' }

$projects = @(dotnet sln RelayCove.sln list | Where-Object { $_ -match '\.csproj$' })
if ($projects.Count -ne 8) { throw "解决方案项目数错误：$($projects.Count)" }

$trackedOutputs = @(git ls-files | rg '(^|/)(bin|obj|TestResults)/')
if ($trackedOutputs.Count -ne 0) { throw '构建输出被跟踪' }

git diff --check '1dd6eb08c1f839bf651a433e9dc647347ef68469..HEAD'
if ($LASTEXITCODE -ne 0) { throw '提交差异空白错误' }
```

### 停止并询问

- .NET 10 SDK 或 WPF/ASP.NET 模板无法在当前 Windows 环境正常还原与构建。
- 需要加入范围外主要依赖、改变项目职责或目标框架。
- 基线或模板带入无法解释的安全配置、密钥或范围外文件。
- 同时遵守 `AGENTS.md` 与 `docs/ai/WORKFLOW.md` 的通用停止条件。

## 执行提示词

```text
只创建最小可构建、可运行和可验证骨架，不实现业务协议。
按本机 .NET 10 模板生成项目，删除 Class1/UnitTest1 等无意义占位，保留最小启动入口与程序集测试。
Fast 和 Full 必须真实执行构建与测试，任何 native 命令非零都使脚本失败。
先运行 Fast，再本地检查点提交；运行 Full、自审和负向验证后记录证据。
不得推送或合并。
```

## 任务结果

### 修改摘要

- 待实施后填写。

### 验证证据

| 状态 | 命令或场景 | 结果 |
| --- | --- | --- |
| `未验证` | `verify.ps1 -Mode Fast` | 待运行 |
| `未验证` | `verify.ps1 -Mode Full` | 待运行 |
| `未验证` | 缺失解决方案负向验证 | 待运行 |
| `未验证` | 项目数、测试数、输出与文件范围 | 待运行 |

### 文件范围

- 新增：待填写。
- 修改：待填写。
- 删除：无。

### 决策与限制

- 决策：阶段 0 只使用模板与框架内置能力；业务依赖由后续最小纵向切片按需引入。
- 已知限制：本任务不证明认证、消息、同步、通知或部署行为。

### 下一步

- 创建 M1 的共享协议最小纵向切片任务。
