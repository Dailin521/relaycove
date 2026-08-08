# 阶段 19：rc.24 Client 包可复现性

## 任务定义

- **状态：** `已完成`
- **版本：** `1.0.0-rc.24`
- **分支：** `agent/stage-19-rc24-stabilization`
- **提交：** `9730a14ea736c83355ec7d8af0a78c5e024c8562`
- **范围：** 只生成本地 Client ZIP、离线验证并记录证据；不生成线上清单、不上传、不推送。

## 结果

- `已验证`：在干净、已提交的 HEAD 上使用 SDK `10.0.101` 分别构建 `artifacts/rc24-build-a` 和 `artifacts/rc24-build-b`。
- `已验证`：两份 `RelayCove.Client-1.0.0-rc.24-win-x64.zip` 字节一致；长度 `165908074` bytes，SHA-256 `057a4683921166e03001d3d4bd0eb1bc2b9591fd84fb59fbcf6c19cbe223c228`。
- `已验证`：`verify-client-release.ps1` 通过包内 manifest、文件 hash、PE x64、自包含运行时、秘密排除与重复归档比较。

## 验证

```powershell
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.24 -OutputRoot ./artifacts/rc24-build-a
pwsh ./scripts/publish-client.ps1 -Version 1.0.0-rc.24 -OutputRoot ./artifacts/rc24-build-b
pwsh ./scripts/verify-client-release.ps1 -Version 1.0.0-rc.24 -OutputRoot ./artifacts/rc24-build-a -CompareOutputRoot ./artifacts/rc24-build-b -ExpectedCommit 9730a14ea736c83355ec7d8af0a78c5e024c8562
```

## 限制与下一步

- `未验证`：rc.23→rc.24 更新交付演练。未找到与已记录长度 `165845207` 和 SHA-256 `3f4384424c2e662299d195aedecc3be5008b7c8967272ce904b5263174b39d89` 匹配的精确 rc.23 ZIP；隔离重建旧提交的结果为 `165907053` bytes、SHA-256 `854838e9e1de2c8deb6335da673ecb3a91f438c9632ec726621fa181240c5b64`，已拒绝作为替代输入。
- owner 决定（2026-08-08）：跳过 rc.23→rc.24 更新演练；该场景保持未验证，不得表述为通过。发布策略、线上清单、推送、合并和部署仍须单独授权。
