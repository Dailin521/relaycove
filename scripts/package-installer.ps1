[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$IsccPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = [xml][IO.File]::ReadAllText((Join-Path $repoRoot "src/RelayCove.App/RelayCove.App.csproj"))
$version = $project.SelectSingleNode("/Project/PropertyGroup/ApplicationDisplayVersion").InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The application version must contain three numeric components."
}
$buildNumber = 0
$buildNode = $project.SelectSingleNode("/Project/PropertyGroup/ApplicationVersion")
if ($null -eq $buildNode -or -not [int]::TryParse($buildNode.InnerText, [ref]$buildNumber) -or $buildNumber -le 0) {
    throw "ApplicationVersion must be a positive integer build number."
}

$compiler = (Get-Item -LiteralPath $IsccPath).FullName
$packageRoot = Join-Path $repoRoot "artifacts/package"
$archiveName = "RichChat-$version-win-x64.zip"
$archivePath = Join-Path $packageRoot $archiveName
$manifestPath = Join-Path $packageRoot "RichChat-$version-win-x64.sha256"
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Run 'pwsh ./scripts/verify.ps1 -Mode Full' before packaging the installer."
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ([IO.File]::ReadAllText($manifestPath).Trim() -cne "$archiveHash  $archiveName") {
    throw "The verified ZIP does not match its SHA-256 manifest."
}

# Stage only the verified release archive, never a developer's bin or user data.
$stageRoot = Join-Path $repoRoot ("artifacts/installer-stage/" + [Guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $stageRoot "payload"
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $payloadRoot)
# Read the verified payload's PE version resource without loading or executing the app.
# The fourth file-version component is emitted from the same ApplicationVersion as
# the runtime RichChatBuildNumber attribute; an old ZIP cannot acquire a new build.
$appAssemblyPath = Join-Path $payloadRoot "RichChat.dll"
if (-not (Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) {
    throw "The verified ZIP is missing RichChat.dll. Rebuild with Full before packaging."
}
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appAssemblyPath)
$payloadDisplayVersion = "$($payloadVersion.FileMajorPart).$($payloadVersion.FileMinorPart).$($payloadVersion.FileBuildPart)"
if ($payloadDisplayVersion -cne $version -or $payloadVersion.FilePrivatePart -ne $buildNumber) {
    throw "The ZIP payload version/build differs from the project. Rebuild with Full before packaging."
}
foreach ($required in @("RichChat.exe", "Assets/RichChat-R.ico", "coreclr.dll", "Microsoft.UI.Xaml.dll", "e_sqlite3.dll", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $required) -PathType Leaf)) {
        throw "Required self-contained runtime file is missing: $required"
    }
}

$definition = Join-Path $PSScriptRoot "installer/RichChat.iss"
$installerName = "RichChat-$version-win-x64-Setup.exe"
# Inno updates the output EXE's resources before compression, and deletes incomplete
# output on failure. Keep each attempt separate from both the payload and old packages.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $outputRoot = Join-Path $stageRoot "attempt-$attempt"
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
    $compileLog = Join-Path $stageRoot "compiler-attempt-$attempt.log"
    $compiledBaseName = "installer-" + [Guid]::NewGuid().ToString("N")
    & $compiler "/DAppVersion=$version" "/DPublishRoot=$payloadRoot" "/DOutputRoot=$outputRoot" "/F$compiledBaseName" $definition 2>&1 |
        Tee-Object -FilePath $compileLog | Out-Host
    $compileExitCode = $LASTEXITCODE
    if ($compileExitCode -eq 0) { break }

    $resourceAccessDenied = [IO.File]::ReadAllText($compileLog).Contains(
        "Resource update error: EndUpdateResource failed (5)", [StringComparison]::Ordinal)
    if (-not $resourceAccessDenied -or $attempt -eq 3) {
        throw "Inno Setup failed with exit code $compileExitCode (attempt $attempt). Compiler log: $compileLog"
    }
    Write-Warning "安装器资源更新被 Windows 拒绝访问（错误 5），2 秒后在新目录重试（$($attempt + 1)/3）。"
    Start-Sleep -Seconds 2
}

$compiledInstaller = Join-Path $outputRoot "$compiledBaseName.exe"
if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf) -or
    (Get-Item -LiteralPath $compiledInstaller).Length -eq 0) {
    throw "Inno Setup did not produce a complete installer. Compiler log: $compileLog"
}
$installerHash = (Get-FileHash -LiteralPath $compiledInstaller -Algorithm SHA256).Hash
$stagedManifest = Join-Path $stageRoot "installer.sha256"
Set-Content -LiteralPath $stagedManifest -Encoding ascii -Value "$installerHash  $installerName"
$stagedUpdateManifest = Join-Path $stageRoot "update-win-x64.json"
& (Join-Path $PSScriptRoot "write-update-manifest.ps1") -InstallerPath $compiledInstaller `
    -OutputPath $stagedUpdateManifest -Version $version -BuildNumber $buildNumber
$installerPath = Join-Path $packageRoot $installerName
$installerManifestPath = Join-Path $packageRoot "RichChat-$version-win-x64-Setup.sha256"
$updateManifestPath = Join-Path $packageRoot "update-win-x64.json"
$previousInstaller = Join-Path $stageRoot "previous-installer.exe"
$hadPreviousInstaller = Test-Path -LiteralPath $installerPath -PathType Leaf
if ($hadPreviousInstaller) { Copy-Item -LiteralPath $installerPath -Destination $previousInstaller }
$hadPreviousManifest = Test-Path -LiteralPath $installerManifestPath -PathType Leaf
if ($hadPreviousManifest) {
    Copy-Item -LiteralPath $installerManifestPath -Destination (Join-Path $stageRoot "previous-installer.sha256")
}
# Same-volume rename publishes only a successful compile. A locked old EXE stays intact.
[IO.File]::Move($compiledInstaller, $installerPath, $true)
try {
    [IO.File]::Move($stagedManifest, $installerManifestPath, $true)
    # Publish discovery metadata last, after both installer and checksum succeeded.
    [IO.File]::Move($stagedUpdateManifest, $updateManifestPath, $true)
}
catch {
    # Restore the previous pair if publishing its manifest fails (for example, a locked file).
    if ($hadPreviousInstaller) { [IO.File]::Move($previousInstaller, $installerPath, $true) }
    else { [IO.File]::Move($installerPath, $compiledInstaller) }
    if ($hadPreviousManifest) {
        # An unchanged, locked checksum already contains the previous value.
        $previousManifest = Join-Path $stageRoot "previous-installer.sha256"
        if ([IO.File]::ReadAllText($installerManifestPath) -cne [IO.File]::ReadAllText($previousManifest)) {
            [IO.File]::Move($previousManifest, $installerManifestPath, $true)
        }
    }
    elseif (Test-Path -LiteralPath $installerManifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $installerManifestPath
    }
    throw
}
Write-Host "Windows installer: $installerPath"
Write-Host "SHA-256: $installerHash"
Write-Host "GitHub Release update manifest: $updateManifestPath"
