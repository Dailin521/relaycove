[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Fast", "Full", "Live")]
    [string]$Mode
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repoRoot "RelayCove.sln"
$iconPath = Join-Path $repoRoot "src/RelayCove.App/Resources/AppIcon/RelayCove.ico"
$webRoot = Join-Path $repoRoot "src/RelayCove.Web"
$webPackageLock = Join-Path $webRoot "package-lock.json"
$webDist = Join-Path $webRoot "dist"
$releaseVersion = "2.2.0"
$releaseApplicationVersion = "3"
$expectedIconLength = 65044
$expectedIconSha256 = "07906CE7D87860C4A15DDD6F904DA722F7BBC3C882DC32FD1D285A78B1161B52"
$solutionProjects = @(
    "src/RelayCove.App/RelayCove.App.csproj",
    "src/RelayCove.Core/RelayCove.Core.csproj",
    "src/RelayCove.Data/RelayCove.Data.csproj",
    "src/RelayCove.Zulip.Client/RelayCove.Zulip.Client.csproj",
    "tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj",
    "tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj",
    "tests/RelayCove.Data.Tests/RelayCove.Data.Tests.csproj",
    "tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj",
    "tests/RelayCove.Zulip.LiveTests/RelayCove.Zulip.LiveTests.csproj"
)
$localTestProjects = @(
    "tests/RelayCove.Core.Tests/RelayCove.Core.Tests.csproj",
    "tests/RelayCove.Zulip.Client.Tests/RelayCove.Zulip.Client.Tests.csproj",
    "tests/RelayCove.Data.Tests/RelayCove.Data.Tests.csproj",
    "tests/RelayCove.App.Tests/RelayCove.App.Tests.csproj"
)

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Npm {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    Push-Location $webRoot
    try {
        & npm @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "npm $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-DotNetRestoreState {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "RelayCove requires the repository-compatible .NET SDK."
    }

    foreach ($project in $solutionProjects) {
        $projectDirectory = Split-Path -Parent (Join-Path $repoRoot $project)
        $assets = Join-Path $projectDirectory "obj/project.assets.json"
        if (-not (Test-Path -LiteralPath $assets -PathType Leaf)) {
            throw "NuGet assets are not provisioned for $project. Run 'dotnet restore RelayCove.sln' explicitly before offline verification."
        }
    }
}

function Assert-WebToolchain {
    if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "RelayCove.Web requires repository-compatible Node.js and npm."
    }
    if (-not (Test-Path -LiteralPath $webPackageLock -PathType Leaf)) {
        throw "RelayCove.Web package-lock.json is missing."
    }

    foreach ($command in @("tsc", "vitest", "vite", "playwright")) {
        $windowsCommand = Join-Path $webRoot "node_modules/.bin/$command.cmd"
        $portableCommand = Join-Path $webRoot "node_modules/.bin/$command"
        if (-not (Test-Path -LiteralPath $windowsCommand -PathType Leaf) -and -not (Test-Path -LiteralPath $portableCommand -PathType Leaf)) {
            throw "RelayCove.Web dependencies are not provisioned. Run 'npm ci' explicitly in src/RelayCove.Web before offline verification."
        }
    }
}

function Assert-WebBuildIntegrity {
    $indexPath = Join-Path $webDist "index.html"
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        throw "RelayCove.Web production index.html is missing."
    }
    if (-not (Get-ChildItem -LiteralPath (Join-Path $webDist "assets") -File -Filter "*.js" -ErrorAction SilentlyContinue)) {
        throw "RelayCove.Web production JavaScript bundle is missing."
    }

    $index = Get-Content -Raw -LiteralPath $indexPath
    if ($index -match '(?i)(src|href)=["''][ ]*https?://') {
        throw "RelayCove.Web production index contains a runtime CDN dependency."
    }
    if ($index -notmatch '(?i)(src|href)=["'']/relaycove-web/assets/index-[^"'']+\.(js|css)') {
        throw "RelayCove.Web production index is not rooted at the fixed /relaycove-web/ deployment path."
    }
    if ($index -match '(?i)(src|href)=["'']/assets/') {
        throw "RelayCove.Web production index contains a root-level asset URL that would bypass the fixed deployment path."
    }
    if ($index -notmatch '(?i)href=["'']/relaycove-web/relaycove\.svg["'']') {
        throw "RelayCove.Web production index is missing its fixed-path bundled favicon."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $webDist "relaycove.svg") -PathType Leaf)) {
        throw "RelayCove.Web production favicon is missing."
    }

    foreach ($fixtureMarker in @(
        "Maya Chen",
        "顶部按微信",
        "Acme Workspace",
        "fixture.invalid",
        "fixture@relaycove.invalid",
        "本地演示数据",
        "演示草稿只保存在当前页面"
    )) {
        $match = Get-ChildItem -LiteralPath $webDist -Recurse -File | Select-String -SimpleMatch -Pattern $fixtureMarker
        if ($match) {
            throw "RelayCove.Web production output contains development fixture data: $fixtureMarker"
        }
    }
}

function Assert-WebCommandLaunchers {
    $launchers = @(
        (Join-Path $repoRoot "start-web-dev.cmd"),
        (Join-Path $repoRoot "deploy-web.cmd")
    )
    foreach ($launcher in $launchers) {
        if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
            throw "RelayCove Web command launcher is missing: $launcher"
        }
        $content = [IO.File]::ReadAllText($launcher)
        if ($content -match '(?<!\r)\n') {
            throw "RelayCove Web command launcher must use Windows CRLF line endings: $launcher"
        }
    }

    if ([IO.File]::ReadAllText($launchers[0]) -notmatch 'npm\.cmd run dev') {
        throw "RelayCove Web local launcher no longer starts the repository Vite dev command."
    }
    if ([IO.File]::ReadAllText($launchers[1]) -notmatch 'scripts\\deploy-web\.ps1') {
        throw "RelayCove Web deployment launcher no longer calls the verified deployment script."
    }
}

function Invoke-WebFastVerification {
    Assert-WebToolchain
    Assert-WebCommandLaunchers
    & (Join-Path $repoRoot "scripts/test-web-deployment-tools.ps1")
    Invoke-Npm run typecheck
    Invoke-Npm run test:unit
    Invoke-Npm run build
    Assert-WebBuildIntegrity
}

function Invoke-WebBrowserVerification {
    Assert-WebToolchain
    Invoke-Npm run test:e2e
}

function Invoke-LocalTests {
    param([Parameter(Mandatory = $true)][string]$Configuration)

    foreach ($project in $localTestProjects) {
        Invoke-DotNet test $project -c $Configuration --no-build --no-restore --nologo --verbosity minimal
    }
}

function Assert-IconIntegrity {
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "The preserved RelayCove icon is missing."
    }

    $icon = Get-Item -LiteralPath $iconPath
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $iconPath).Hash
    if ($icon.Length -ne $expectedIconLength -or $hash -cne $expectedIconSha256) {
        throw "The preserved RelayCove icon no longer matches the frozen byte/hash baseline."
    }
}

function Assert-ReleaseVersionMetadata {
    $appProjectPath = Join-Path $repoRoot "src/RelayCove.App/RelayCove.App.csproj"
    $project = [xml][IO.File]::ReadAllText($appProjectPath)
    $displayVersions = @($project.SelectNodes("/Project/PropertyGroup/ApplicationDisplayVersion") | ForEach-Object { $_.InnerText })
    $applicationVersions = @($project.SelectNodes("/Project/PropertyGroup/ApplicationVersion") | ForEach-Object { $_.InnerText })
    $informationalVersions = @($project.SelectNodes("/Project/PropertyGroup/InformationalVersion") | ForEach-Object { $_.InnerText })

    if ($displayVersions.Count -ne 1 -or $displayVersions[0] -cne $releaseVersion) {
        throw "ApplicationDisplayVersion must match release version $releaseVersion."
    }
    if ($informationalVersions.Count -ne 1 -or $informationalVersions[0] -cne $releaseVersion) {
        throw "InformationalVersion must match release version $releaseVersion."
    }
    if ($applicationVersions.Count -ne 1 -or $applicationVersions[0] -cne $releaseApplicationVersion) {
        throw "ApplicationVersion must match release application version $releaseApplicationVersion."
    }
}

function Invoke-FastVerification {
    Assert-IconIntegrity
    Assert-DotNetRestoreState
    Invoke-DotNet build $solution -c Debug --no-restore --nologo --verbosity minimal /p:ContinuousIntegrationBuild=true
    Invoke-LocalTests -Configuration Debug
}

function Assert-ArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate a path outside the repository artifact directory: $resolved"
    }
}

function Test-ContainsBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Haystack,
        [Parameter(Mandatory = $true)][byte[]]$Needle
    )

    if ($Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) {
        return $false
    }

    for ($index = 0; $index -le $Haystack.Length - $Needle.Length; $index++) {
        $matches = $true
        for ($needleIndex = 0; $needleIndex -lt $Needle.Length; $needleIndex++) {
            if ($Haystack[$index + $needleIndex] -ne $Needle[$needleIndex]) {
                $matches = $false
                break
            }
        }

        if ($matches) {
            return $true
        }
    }

    return $false
}

function Assert-NoSecrets {
    param([Parameter(Mandatory = $true)][string]$PublishRoot)

    $forbiddenFiles = Get-ChildItem -LiteralPath $PublishRoot -Recurse -File | Where-Object {
        $_.Extension -in @(".db", ".log", ".env") -or
        $_.Name -match "(?i)testresults|securestorage|credential|secret"
    }
    if ($forbiddenFiles) {
        throw "Forbidden runtime/user-data files found in publish output: $($forbiddenFiles.FullName -join ', ')"
    }

    $textPatterns = @(
        '(?i)"api[_-]?key"\s*:\s*"[^"\r\n]+"',
        '(?i)"password"\s*:\s*"[^"\r\n]+"',
        '(?i)authorization\s*:\s*basic\s+[a-z0-9+/=]+'
    )
    $textExtensions = @(".json", ".config", ".xml", ".txt", ".md", ".yml", ".yaml")
    foreach ($file in Get-ChildItem -LiteralPath $PublishRoot -Recurse -File | Where-Object { $_.Extension -in $textExtensions }) {
        $content = Get-Content -Raw -LiteralPath $file.FullName
        foreach ($pattern in $textPatterns) {
            if ($content -match $pattern) {
                throw "Potential plaintext credential found in $($file.FullName)."
            }
        }
    }

    $secretEnvironmentVariables = @(
        "RELAYCOVE_LIVE_USER_A_API_KEY",
        "RELAYCOVE_LIVE_USER_A_PASSWORD",
        "RELAYCOVE_LIVE_USER_B_API_KEY",
        "RELAYCOVE_LIVE_BOOTSTRAP_API_KEY",
        "RELAYCOVE_LIVE_PASSWORD"
    )
    $publishFiles = Get-ChildItem -LiteralPath $PublishRoot -Recurse -File
    foreach ($name in $secretEnvironmentVariables) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $needle = [Text.Encoding]::UTF8.GetBytes($value)
        foreach ($file in $publishFiles) {
            if (Test-ContainsBytes -Haystack ([IO.File]::ReadAllBytes($file.FullName)) -Needle $needle) {
                throw "Value from $name was found in $($file.FullName)."
            }
        }
    }
}

function Invoke-FullVerification {
    Assert-ReleaseVersionMetadata
    Assert-IconIntegrity
    Assert-DotNetRestoreState
    Invoke-DotNet build $solution -c Release --no-restore --nologo --verbosity minimal /p:ContinuousIntegrationBuild=true
    Invoke-LocalTests -Configuration Release

    $artifactRoot = Join-Path $repoRoot "artifacts"
    $publishRoot = Join-Path $artifactRoot "publish/win-x64"
    $packageRoot = Join-Path $artifactRoot "package"
    Assert-ArtifactPath -Path $publishRoot
    Assert-ArtifactPath -Path $packageRoot
    foreach ($path in @($publishRoot, $packageRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    Invoke-DotNet publish "src/RelayCove.App/RelayCove.App.csproj" `
        -c Release `
        -f net10.0-windows10.0.19041.0 `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        --nologo `
        --verbosity minimal `
        /p:RuntimeIdentifierOverride=win-x64 `
        /p:WindowsPackageType=None `
        /p:WindowsAppSDKSelfContained=true `
        /p:DebugSymbols=false `
        /p:DebugType=None `
        --output $publishRoot

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $publishRoot

    foreach ($required in @("RelayCove.App.exe", "e_sqlite3.dll", "coreclr.dll", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        if (-not (Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter $required)) {
            throw "Required self-contained publish file is missing: $required"
        }
    }
    Assert-NoSecrets -PublishRoot $publishRoot

    $zipPath = Join-Path $packageRoot "RelayCove-$releaseVersion-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
    $manifestPath = Join-Path $packageRoot "RelayCove-$releaseVersion-win-x64.sha256"
    Set-Content -LiteralPath $manifestPath -Encoding ascii -NoNewline -Value "$hash  RelayCove-$releaseVersion-win-x64.zip"
    Write-Host "Windows package: $zipPath"
    Write-Host "SHA-256: $hash"
}

function Invoke-LiveVerification {
    $required = @(
        "RELAYCOVE_LIVE_REALM",
        "RELAYCOVE_LIVE_USER_A_EMAIL",
        "RELAYCOVE_LIVE_USER_A_ID",
        "RELAYCOVE_LIVE_USER_A_API_KEY",
        "RELAYCOVE_LIVE_USER_A_PASSWORD",
        "RELAYCOVE_LIVE_USER_B_EMAIL",
        "RELAYCOVE_LIVE_USER_B_ID",
        "RELAYCOVE_LIVE_USER_B_API_KEY",
        "RELAYCOVE_LIVE_CHANNEL_ID",
        "RELAYCOVE_LIVE_CHANNEL_NAME",
        "RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_ID",
        "RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_NAME",
        "RELAYCOVE_LIVE_JOINABLE_CHANNEL_ID",
        "RELAYCOVE_LIVE_JOINABLE_CHANNEL_NAME",
        "RELAYCOVE_LIVE_ALLOWED_USER_IDS",
        "RELAYCOVE_LIVE_CHANNEL_APPROVED",
        "RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_APPROVED",
        "RELAYCOVE_LIVE_JOINABLE_CHANNEL_APPROVED",
        "RELAYCOVE_LIVE_STAGE23_APPROVED"
    )
    foreach ($name in $required) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Live verification is fail-closed: missing $name."
        }
    }
    if ([Environment]::GetEnvironmentVariable("RELAYCOVE_LIVE_WRITE_CONFIRM") -cne "I_UNDERSTAND_THIS_WRITES_TO_ZULIP") {
        throw "Live verification is fail-closed: RELAYCOVE_LIVE_WRITE_CONFIRM does not contain the exact confirmation value."
    }
    if ([Environment]::GetEnvironmentVariable("RELAYCOVE_LIVE_CHANNEL_APPROVED") -cne "true") {
        throw "Live verification requires an independently approved private E2E channel."
    }
    if ([Environment]::GetEnvironmentVariable("RELAYCOVE_LIVE_UNSUBSCRIBE_CHANNEL_APPROVED") -cne "true") {
        throw "Live verification requires an independently approved private unsubscribe channel."
    }
    if ([Environment]::GetEnvironmentVariable("RELAYCOVE_LIVE_JOINABLE_CHANNEL_APPROVED") -cne "true" -or [Environment]::GetEnvironmentVariable("RELAYCOVE_LIVE_STAGE23_APPROVED") -cne "true") {
        throw "Live verification requires explicit Stage23 joinable-channel approval."
    }

    Invoke-DotNet restore "tests/RelayCove.Zulip.LiveTests/RelayCove.Zulip.LiveTests.csproj" --nologo
    Invoke-DotNet test "tests/RelayCove.Zulip.LiveTests/RelayCove.Zulip.LiveTests.csproj" `
        -c Release `
        --no-restore `
        --nologo `
        --verbosity minimal
}

Push-Location $repoRoot
try {
    switch ($Mode) {
        "Fast" { Invoke-FastVerification }
        "Full" { Invoke-FullVerification }
        "Live" { Invoke-LiveVerification }
    }
}
finally {
    Pop-Location
}
