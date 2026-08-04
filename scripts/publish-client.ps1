[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"),

    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$clientProject = Join-Path $repositoryRoot "src\RelayCove.Client\RelayCove.Client.csproj"
$updaterProject = Join-Path $repositoryRoot "src\RelayCove.Updater\RelayCove.Updater.csproj"
$runtimeIdentifier = "win-x64"
$archivePrefix = "RelayCove.Client"
$zipTimestamp = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
$normalFileAttributes = "00000080"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Assert-ReleaseVersion {
    param([Parameter(Mandatory)][string] $Value)

    $identifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
    $semVerPattern = "\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-$identifier(?:\.$identifier)*)?\z"
    if ($Value.Length -gt 64 -or $Value -notmatch $semVerPattern) {
        throw "Version must be a 1-64 character SemVer value: major.minor.patch with an optional prerelease, without build metadata."
    }
}

function Resolve-PathInside {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Description,
        [switch] $AllowRoot
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    if ((-not $AllowRoot -or -not $resolvedPath.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) -and
        -not $resolvedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain inside $resolvedRoot."
    }

    return $resolvedPath
}

function Assert-NoReparsePointAncestors {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Root
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release paths must not use a reparse-point artifacts root: $Root"
    }

    $relativePath = [System.IO.Path]::GetRelativePath($Root, $Path)
    $currentPath = $Root
    foreach ($segment in $relativePath.Split([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -eq ".") {
            continue
        }
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }
        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release paths must not traverse a reparse point: $currentPath"
        }
    }
}

function Get-GitOutput {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $value = & git -C $repositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }

    return ($value | Out-String).Trim()
}

function Test-GitClean {
    $status = & git -C $repositoryRoot status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed with exit code $LASTEXITCODE"
    }
    return [string]::IsNullOrWhiteSpace(($status | Out-String))
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Assert-WindowsX64Pe {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Description
    )

    $fileInfo = Get-Item -LiteralPath $Path -Force
    if ($fileInfo.Length -lt 64) {
        throw "Release package file '$Description' is too short to be a Windows PE executable."
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $dosHeader = [byte[]]::new(64)
        $stream.ReadExactly($dosHeader, 0, $dosHeader.Length)
        $peOffset = [System.BitConverter]::ToInt32($dosHeader, 60)
        if ($dosHeader[0] -ne 0x4d -or $dosHeader[1] -ne 0x5a -or
            $peOffset -lt 64 -or $peOffset -gt ($fileInfo.Length - 6)) {
            throw "Release package file '$Description' is not a valid Windows PE executable."
        }

        $stream.Position = $peOffset
        $peHeader = [byte[]]::new(6)
        $stream.ReadExactly($peHeader, 0, $peHeader.Length)
        if ($peHeader[0] -ne 0x50 -or $peHeader[1] -ne 0x45 -or
            $peHeader[2] -ne 0 -or $peHeader[3] -ne 0 -or
            [System.BitConverter]::ToUInt16($peHeader, 4) -ne 0x8664) {
            throw "Release package file '$Description' is not an x64 Windows PE executable."
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-SortedPackageFiles {
    param([Parameter(Mandatory)][string] $PackageRoot)

    $filesByPath = @{}
    foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release package must not contain a reparse point: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            continue
        }

        $relativePath = Get-RelativePath $PackageRoot $item.FullName
        if ([System.IO.Path]::IsPathFullyQualified($relativePath) -or
            $relativePath.Split('/') -contains ".." -or
            $filesByPath.ContainsKey($relativePath)) {
            throw "Release package contains an unsafe or duplicate path '$relativePath'."
        }
        $filesByPath.Add($relativePath, $item)
    }

    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $filesByPath.Keys) {
        $paths.Add([string]$path)
    }
    $paths.Sort([System.StringComparer]::Ordinal)
    return @($paths | ForEach-Object {
            [pscustomobject]@{
                RelativePath = $_
                File = $filesByPath[$_]
            }
        })
}

function Assert-PackagePaths {
    param([Parameter(Mandatory)][string] $PackageRoot)

    $requiredFiles = @(
        "RelayCove.Client.exe",
        "RelayCove.Updater.exe",
        "RelayCove.Client.dll",
        "RelayCove.Client.deps.json",
        "RelayCove.Client.runtimeconfig.json",
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "Microsoft.WindowsAppRuntime.Bootstrap.dll",
        "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.ui.xaml.dll",
        "Microsoft.UI.Xaml.Controls.dll",
        "Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll",
        "WinRT.Runtime.dll",
        "e_sqlite3.dll"
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relativePath) -PathType Leaf)) {
            throw "Release package is missing required runtime file '$relativePath'."
        }
    }

    foreach ($fileInfo in Get-SortedPackageFiles $PackageRoot) {
        $relativePath = $fileInfo.RelativePath
        if ($relativePath -match '(?i)(^|/)(bin|obj|uploads|logs|cache)(/|$)|\.pdb$|\.cs$|\.csproj$|\.sln$|\.(db|sqlite)(-wal|-shm)?$|(^|/)\.env(?:\.[^/]*)?$|(^|/)[^/]*secret[^/]*\.json$|(^|/)[^/]*(credential|refresh[-_.]?token|access[-_.]?token)[^/]*\.(json|dat|bin)$|\.(pfx|p12|pem|key|user|bak|tmp)$') {
            throw "Release package contains forbidden file '$relativePath'."
        }
    }

    Assert-WindowsX64Pe (Join-Path $PackageRoot "RelayCove.Client.exe") "RelayCove.Client.exe"
    $updaterPath = Join-Path $PackageRoot "RelayCove.Updater.exe"
    $updaterLength = (Get-Item -LiteralPath $updaterPath -Force).Length
    if ($updaterLength -lt 1MB -or $updaterLength -gt 1GB) {
        throw "Release package updater must be a standalone executable between 1 MiB and 1 GiB."
    }
    Assert-WindowsX64Pe $updaterPath "RelayCove.Updater.exe"
}

function Get-PackageFiles {
    param([Parameter(Mandatory)][string] $PackageRoot)

    return @(
        Get-SortedPackageFiles $PackageRoot |
            ForEach-Object {
                [ordered]@{
                    path = $_.RelativePath
                    length = $_.File.Length
                    sha256 = (Get-FileHash -LiteralPath $_.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    attributes = $normalFileAttributes
                }
            }
    )
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string] $PackageRoot,
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $PackageName,
        [Parameter(Mandatory)][byte[]] $ManifestBytes
    )

    $archiveStream = $null
    $archive = $null
    try {
        $archiveStream = [System.IO.File]::Open($ArchivePath, [System.IO.FileMode]::CreateNew)
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false,
            [System.Text.UTF8Encoding]::new($false))

        $manifestWritten = $false
        foreach ($fileInfo in Get-SortedPackageFiles $PackageRoot) {
            if (-not $manifestWritten -and
                [string]::CompareOrdinal("manifest.json", $fileInfo.RelativePath) -lt 0) {
                $manifestEntry = $archive.CreateEntry(
                    "$PackageName/manifest.json",
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $manifestEntry.LastWriteTime = $zipTimestamp
                $manifestEntry.ExternalAttributes = 0x00000080
                $manifestStream = $manifestEntry.Open()
                try {
                    $manifestStream.Write($ManifestBytes, 0, $ManifestBytes.Length)
                }
                finally {
                    $manifestStream.Dispose()
                }
                $manifestWritten = $true
            }

            $entry = $archive.CreateEntry(
                "$PackageName/$($fileInfo.RelativePath)",
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $zipTimestamp
            $entry.ExternalAttributes = 0x00000080
            $entryStream = $entry.Open()
            $fileStream = [System.IO.File]::OpenRead($fileInfo.File.FullName)
            try {
                $fileStream.CopyTo($entryStream)
            }
            finally {
                $fileStream.Dispose()
                $entryStream.Dispose()
            }
        }

        if (-not $manifestWritten) {
            $manifestEntry = $archive.CreateEntry(
                "$PackageName/manifest.json",
                [System.IO.Compression.CompressionLevel]::Optimal)
            $manifestEntry.LastWriteTime = $zipTimestamp
            $manifestEntry.ExternalAttributes = 0x00000080
            $manifestStream = $manifestEntry.Open()
            try {
                $manifestStream.Write($ManifestBytes, 0, $ManifestBytes.Length)
            }
            finally {
                $manifestStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        elseif ($null -ne $archiveStream) {
            $archiveStream.Dispose()
        }
    }
}

Assert-ReleaseVersion $Version
if (-not (Test-Path -LiteralPath $clientProject -PathType Leaf)) {
    throw "Client project was not found: $clientProject"
}
if (-not (Test-Path -LiteralPath $updaterProject -PathType Leaf)) {
    throw "Updater project was not found: $updaterProject"
}

$resolvedOutputRoot = Resolve-PathInside $OutputRoot $artifactsRoot "OutputRoot" -AllowRoot
Assert-NoReparsePointAncestors $resolvedOutputRoot $artifactsRoot
$sourceTreeClean = Test-GitClean
if (-not $sourceTreeClean -and -not $AllowDirty) {
    throw "Refusing to publish from a dirty Git checkout. Use -AllowDirty only for non-release local validation."
}

$commit = Get-GitOutput @("rev-parse", "--verify", "HEAD")
$sdkVersion = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "Unable to determine the .NET SDK version."
}

$packageName = "$archivePrefix-$Version-$runtimeIdentifier"
$finalContainer = Join-Path (Join-Path $resolvedOutputRoot "client") $Version
$stagingRoot = Resolve-PathInside (
    Join-Path $artifactsRoot (Join-Path ".staging" ([System.Guid]::NewGuid().ToString("N")))) $artifactsRoot "staging directory"
$stagedContainer = Join-Path (Join-Path $stagingRoot "client") $Version
$dotnetArtifactsPath = Join-Path $stagingRoot ".dotnet-artifacts"
$updaterPublishRoot = Join-Path $stagingRoot ".updater-publish"
$packageRoot = Join-Path $stagedContainer $packageName
$archiveFileName = "$packageName.zip"
$archivePath = Join-Path $stagedContainer $archiveFileName

if (Test-Path -LiteralPath $finalContainer) {
    throw "Release output already exists and will not be overwritten: $finalContainer"
}

try {
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    Assert-NoReparsePointAncestors $stagingRoot $artifactsRoot
    Assert-NoReparsePointAncestors $finalContainer $artifactsRoot
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

    $commonProperties = @(
        "/p:Version=$Version",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:ContinuousIntegrationBuild=true",
        "/p:SelfContained=true",
        "/p:WindowsAppSDKSelfContained=true",
        "/p:PublishSingleFile=false",
        "/p:PublishTrimmed=false",
        "/p:PublishReadyToRun=false"
    )
    Invoke-Checked "dotnet" @(
        "restore", $clientProject,
        "--runtime", $runtimeIdentifier,
        "--artifacts-path", $dotnetArtifactsPath,
        "/p:Version=$Version",
        "/p:WindowsAppSDKSelfContained=true"
    )
    Invoke-Checked "dotnet" (@(
            "publish", $clientProject,
            "--configuration", "Release",
            "--runtime", $runtimeIdentifier,
            "--self-contained", "true",
            "--artifacts-path", $dotnetArtifactsPath,
            "--no-restore",
            "--output", $packageRoot) + $commonProperties)

    Invoke-Checked "dotnet" @(
        "publish", $updaterProject,
        "--configuration", "Release",
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "--artifacts-path", $dotnetArtifactsPath,
        "--output", $updaterPublishRoot,
        "/p:Version=$Version",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:ContinuousIntegrationBuild=true",
        "/p:SelfContained=true",
        "/p:PublishSingleFile=true",
        "/p:PublishTrimmed=false",
        "/p:PublishReadyToRun=false")

    $updaterPublishItems = @(Get-ChildItem -LiteralPath $updaterPublishRoot -Force)
    if ($updaterPublishItems.Count -ne 1 -or $updaterPublishItems[0].PSIsContainer -or
        ($updaterPublishItems[0].Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $updaterPublishItems[0].Name -ne "RelayCove.Updater.exe") {
        throw "Standalone updater publish must contain only RelayCove.Updater.exe."
    }
    $updaterFile = $updaterPublishItems[0]
    $packageUpdaterPath = Join-Path $packageRoot "RelayCove.Updater.exe"
    if (Test-Path -LiteralPath $packageUpdaterPath) {
        throw "Client publish unexpectedly produced RelayCove.Updater.exe."
    }
    Copy-Item -LiteralPath $updaterFile.FullName -Destination $packageUpdaterPath

    Assert-PackagePaths $packageRoot
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        commit = $commit
        sourceTreeClean = $sourceTreeClean
        rid = $runtimeIdentifier
        selfContained = $true
        windowsAppSdkSelfContained = $true
        sdkVersion = $sdkVersion
        packageRoot = $packageName
        files = @(Get-PackageFiles $packageRoot)
    }
    $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($manifest | ConvertTo-Json -Depth 6) + [System.Environment]::NewLine)
    New-DeterministicZip $packageRoot $archivePath $packageName $manifestBytes
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$archivePath.sha256",
        "$archiveHash  $archiveFileName$([System.Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path (Split-Path -Parent $finalContainer) -Force | Out-Null
    Move-Item -LiteralPath $stagedContainer -Destination (Split-Path -Parent $finalContainer)
    $stagedContainer = $null
    Write-Host "RelayCove Client release package created: $(Join-Path $finalContainer $archiveFileName)"
}
finally {
    if ($null -ne $stagedContainer -and (Test-Path -LiteralPath $stagedContainer)) {
        Assert-NoReparsePointAncestors $stagedContainer $artifactsRoot
        Remove-Item -LiteralPath $stagedContainer -Recurse -Force
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-NoReparsePointAncestors $stagingRoot $artifactsRoot
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
