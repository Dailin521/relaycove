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
$expectedIconLength = 65044
$expectedIconSha256 = "07906CE7D87860C4A15DDD6F904DA722F7BBC3C882DC32FD1D285A78B1161B52"
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

function Invoke-FastVerification {
    Assert-IconIntegrity
    Invoke-DotNet restore $solution --nologo
    Invoke-DotNet format $solution --verify-no-changes --no-restore --verbosity minimal
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
    Invoke-FastVerification
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

    $zipPath = Join-Path $packageRoot "RelayCove-2.0.0-alpha.1-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
    $manifestPath = Join-Path $packageRoot "RelayCove-2.0.0-alpha.1-win-x64.sha256"
    Set-Content -LiteralPath $manifestPath -Encoding ascii -NoNewline -Value "$hash  RelayCove-2.0.0-alpha.1-win-x64.zip"
    Write-Host "Windows package: $zipPath"
    Write-Host "SHA-256: $hash"
}

function Invoke-LiveVerification {
    $required = @(
        "RELAYCOVE_LIVE_REALM",
        "RELAYCOVE_LIVE_USER_A_EMAIL",
        "RELAYCOVE_LIVE_USER_A_ID",
        "RELAYCOVE_LIVE_USER_A_API_KEY",
        "RELAYCOVE_LIVE_USER_B_EMAIL",
        "RELAYCOVE_LIVE_USER_B_ID",
        "RELAYCOVE_LIVE_USER_B_API_KEY",
        "RELAYCOVE_LIVE_CHANNEL_ID",
        "RELAYCOVE_LIVE_ALLOWED_USER_IDS",
        "RELAYCOVE_LIVE_CHANNEL_APPROVED"
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
