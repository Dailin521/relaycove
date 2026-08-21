# Stage 24.12 — 私聊详情简化

Date: 2026-08-21
Status: local candidate; Visual Studio confirmation pending

## Problem

私聊详情把“会话状态”“已接通能力”“能力边界”以及 Realm、presence、共同频道和缓存推断等开发者信息直接展示给用户，既重复又占据主要空间。用户要求将私聊详情简化为真正面向用户的信息。

这是本轮唯一问题。Stage 24.11 的未提交候选原样保留；频道详情、频道动作、协议、同步和 Realm 行为不在本轮改变。

## Implementation

- 私聊只保留顶部身份区，不再显示频道使用的会话状态、能力说明、能力边界和动作区。
- 1:1 私信显示“私信”、对方姓名和“与 … 的私信”。
- self-DM 显示“给自己”、当前用户名（自己）和“仅你自己可见”。
- 群组私信显示“群组私信”、参与者标题以及包含当前用户在内的人数和姓名列表。
- 群聊参与者文案直接由规范参与者 ID 与可靠用户映射生成，不从逗号分隔的标题反解析姓名。
- 频道详情仍显示既有状态、已接通能力、能力边界和频道动作。

## Deterministic evidence

```powershell
dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-12-details-tests/ --filter "FullyQualifiedName~DetailsPaneViewTests|FullyQualifiedName~Projection_WhenOneToOneDirectMessageIsSelected|FullyQualifiedName~Projection_WhenSelfDirectMessageIsSelected|FullyQualifiedName~Projection_WhenGroupDirectMessageIsSelected"
# 4/4 passed; App/XAML/resources built successfully

dotnet test tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj -c Debug --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.verify/stage24-12-final-app-tests/
# 204/204 passed
```

未运行 Fast、Full、Live、打包、生产 Realm 或真实写入。未启动、移动、操作或截图用户的 MAUI 窗口。

独立只读复核确认当前没有 P0/P1；其提出的群聊姓名分隔符和定向断言 P2 已在最终测试前处理。

## Visual Studio short check

1. 打开一个 1:1 私信的详情：只应看到“私信”、对方姓名和“与对方的私信”，不应出现会话状态、已接通能力或能力边界。
2. 打开 self-DM：应显示“给自己”、自己的姓名和“仅你自己可见”。
3. 如果有群组私信，打开详情：应显示群组参与者姓名，以及包含“你”的正确总人数。
4. 打开频道话题详情：确认原有会话状态、能力说明和频道动作仍在。

Manual result: pending user Visual Studio confirmation.
