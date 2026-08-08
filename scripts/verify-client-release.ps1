[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"),

    [string] $CompareOutputRoot,

    [string] $ExpectedCommit,

    [switch] $AllowDirtySource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$runtimeIdentifier = "win-x64"
$archivePrefix = "RelayCove.Client"
$maximumTextEntryBytes = 1MB
$maximumArchiveEntries = 2048
$maximumUncompressedBytes = 2GB

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

function Test-ForbiddenPath {
    param([Parameter(Mandatory)][string] $RelativePath)

    return $RelativePath -match '(?i)(^|/)(bin|obj|data|uploads|logs|cache|temp)(/|$)|\.pdb$|\.cs$|\.csproj$|\.sln$|\.user$|(^|/)appsettings\.(?!json$)|(^|/).*\.(db|sqlite)(-wal|-shm)?$|(^|/)\.env(?:\.|$)|(^|/)[^/]*secret[^/]*\.json$|(^|/)relaycove-credential\.v1\.bin$|(^|/)[^/]*(credential|refresh[-_.]?token|access[-_.]?token)[^/]*\.(bin|json|dat)$|\.(pfx|p12|pem|key|bak|tmp)$|(^|/)(appdata|localappdata|roaming)(/|$)'
}

function Get-StreamInspection {
    param([Parameter(Mandatory)][System.IO.Stream] $Stream)

    $sha256 = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(81920)
        $prefix = [byte[]]::new(512)
        [long] $length = 0
        $prefixLength = 0
        while (($bytesRead = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($prefixLength -lt $prefix.Length) {
                $bytesToCopy = [Math]::Min($bytesRead, $prefix.Length - $prefixLength)
                [System.Array]::Copy($buffer, 0, $prefix, $prefixLength, $bytesToCopy)
                $prefixLength += $bytesToCopy
            }
            $sha256.AppendData($buffer, 0, $bytesRead)
            $length += $bytesRead
        }
        if ($prefixLength -ne $prefix.Length) {
            $shortPrefix = [byte[]]::new($prefixLength)
            [System.Array]::Copy($prefix, $shortPrefix, $prefixLength)
            $prefix = $shortPrefix
        }
        return [pscustomobject]@{
            Hash = ([System.Convert]::ToHexString($sha256.GetHashAndReset())).ToLowerInvariant()
            Length = $length
            Prefix = $prefix
        }
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-TextEntry {
    param(
        [Parameter(Mandatory)][System.IO.Compression.ZipArchiveEntry] $Entry,
        [Parameter(Mandatory)][string] $EntryName
    )

    if ($Entry.Length -gt $maximumTextEntryBytes) {
        throw "Archive text entry '$EntryName' exceeds $maximumTextEntryBytes bytes."
    }
    $stream = $Entry.Open()
    try {
        $inspection = Get-StreamInspection $stream
    }
    finally {
        $stream.Dispose()
    }
    if ($inspection.Length -ne $Entry.Length) {
        throw "Archive entry '$EntryName' ended before or after its declared length."
    }
    try {
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($inspection.Prefix)
    }
    catch [System.Text.DecoderFallbackException] {
        throw "Archive text entry '$EntryName' is not valid UTF-8."
    }
    if ($inspection.Length -gt $inspection.Prefix.Length) {
        $stream = $Entry.Open()
        try {
            $bytes = [byte[]]::new([int]$Entry.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $bytesRead = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($bytesRead -eq 0) {
                    throw "Archive text entry '$EntryName' ended before its declared length."
                }
                $offset += $bytesRead
            }
            $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
            $inspection.Prefix = $bytes[0..([Math]::Min($bytes.Length, 512) - 1)]
        }
        finally {
            $stream.Dispose()
        }
    }
    return [pscustomobject]@{ Text = $text; Inspection = $inspection }
}

function Read-Archive {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $PackageName
    )

    $archiveStream = $null
    $archive = $null
    try {
        $archiveStream = [System.IO.File]::OpenRead($ArchivePath)
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $entries = [System.Collections.Generic.List[object]]::new()
        $textEntries = @{}
        $manifestJson = $null
        [long] $totalLength = 0

        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName
            if ($entries.Count -ge $maximumArchiveEntries) {
                throw "Archive exceeds the maximum entry count of $maximumArchiveEntries."
            }
            $isDirectory = $entryName.EndsWith('/', [System.StringComparison]::Ordinal)
            $segments = $entryName.Split('/')
            $unsafeSegment = $false
            for ($index = 0; $index -lt $segments.Length; $index++) {
                if (($segments[$index] -eq "." -or $segments[$index] -eq ".." -or [string]::IsNullOrEmpty($segments[$index])) -and
                    -not ($isDirectory -and $index -eq ($segments.Length - 1))) {
                    $unsafeSegment = $true
                    break
                }
            }
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                $entryName.Contains('\', [System.StringComparison]::Ordinal) -or
                [System.IO.Path]::IsPathFullyQualified($entryName) -or
                $unsafeSegment -or
                ($entryName -ne "$PackageName/" -and -not $entryName.StartsWith("$PackageName/", [System.StringComparison]::Ordinal)) -or
                -not $seen.Add($entryName)) {
                throw "Archive contains an unsafe or duplicate entry '$entryName'."
            }

            $entryTimestamp = $entry.LastWriteTime.DateTime
            if ($entryTimestamp.Year -ne 1980 -or $entryTimestamp.Month -ne 1 -or $entryTimestamp.Day -ne 1 -or
                $entryTimestamp.Hour -ne 0 -or $entryTimestamp.Minute -ne 0 -or $entryTimestamp.Second -ne 0) {
                throw "Archive entry '$entryName' does not have the fixed ZIP timestamp."
            }
            if (($entry.ExternalAttributes -band 0x400) -ne 0) {
                throw "Archive entry '$entryName' must not be a reparse point."
            }

            if ($isDirectory) {
                if ($entry.Length -ne 0) {
                    throw "Archive directory entry '$entryName' contains data."
                }
                $entries.Add([pscustomobject]@{ name = $entryName; isDirectory = $true; length = 0; sha256 = $null; prefix = $null; attributes = $entry.ExternalAttributes })
                continue
            }
            if ($entry.ExternalAttributes -ne 0x00000080) {
                throw "Archive file '$entryName' has unexpected Windows attributes."
            }
            if ($entry.Length -lt 0 -or $entry.Length -gt ($maximumUncompressedBytes - $totalLength)) {
                throw "Archive exceeds the maximum uncompressed size of $maximumUncompressedBytes bytes."
            }
            $totalLength += $entry.Length

            if ($entryName -eq "$PackageName/manifest.json" -or
                $entryName -eq "$PackageName/RelayCove.Client.runtimeconfig.json") {
                $textEntry = Get-TextEntry $entry $entryName
                if ($entryName -eq "$PackageName/manifest.json") {
                    $manifestJson = $textEntry.Text
                }
                else {
                    $textEntries[$entryName] = $textEntry.Text
                }
                $inspection = $textEntry.Inspection
            }
            else {
                $stream = $entry.Open()
                try {
                    $inspection = Get-StreamInspection $stream
                }
                finally {
                    $stream.Dispose()
                }
            }
            if ($inspection.Length -ne $entry.Length) {
                throw "Archive entry '$entryName' ended before or after its declared length."
            }
            $entries.Add([pscustomobject]@{
                    name = $entryName
                    isDirectory = $false
                    length = $inspection.Length
                    sha256 = $inspection.Hash
                    prefix = $inspection.Prefix
                    attributes = $entry.ExternalAttributes
                })
        }

        if ($null -eq $manifestJson) {
            throw "Archive does not contain manifest.json."
        }
        return [pscustomobject]@{ Entries = @($entries); ManifestJson = $manifestJson; TextEntries = $textEntries }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        elseif ($null -ne $archiveStream) { $archiveStream.Dispose() }
    }
}

function Assert-ArchiveOrdering {
    param([Parameter(Mandatory)][object[]] $Entries)

    $previousName = $null
    foreach ($entry in $Entries) {
        if ($null -ne $previousName -and [string]::CompareOrdinal($previousName, $entry.name) -ge 0) {
            throw "Archive entries are not strictly ordinal sorted."
        }
        $previousName = $entry.name
    }
}

function Assert-SelfContainedRuntimeConfiguration {
    param([Parameter(Mandatory)][string] $Content)

    $configuration = $Content | ConvertFrom-Json -AsHashtable
    if (-not $configuration.ContainsKey("runtimeOptions") -or
        $configuration.runtimeOptions -isnot [System.Collections.IDictionary]) {
        throw "Client runtime configuration does not contain runtimeOptions."
    }

    $runtimeOptions = $configuration.runtimeOptions
    if ($runtimeOptions.ContainsKey("framework") -or $runtimeOptions.ContainsKey("frameworks")) {
        throw "Client runtime configuration depends on a shared framework instead of the packaged runtime."
    }
    if (-not $runtimeOptions.ContainsKey("includedFrameworks")) {
        throw "Client runtime configuration does not declare includedFrameworks."
    }

    $includedFrameworks = @($runtimeOptions.includedFrameworks)
    if ($includedFrameworks.Count -ne 2) {
        throw "Client runtime configuration must contain exactly two included frameworks."
    }
    $frameworksByName = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($framework in $includedFrameworks) {
        if ($framework -isnot [System.Collections.IDictionary] -or
            $framework.name -isnot [string] -or
            $framework.version -isnot [string] -or
            [string]::IsNullOrWhiteSpace($framework.version) -or
            -not $frameworksByName.TryAdd($framework.name, $framework.version)) {
            throw "Client runtime configuration contains an invalid included framework."
        }
    }
    foreach ($requiredFramework in @("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")) {
        if (-not $frameworksByName.ContainsKey($requiredFramework)) {
            throw "Client runtime configuration is missing included framework '$requiredFramework'."
        }
    }
}

function Assert-WindowsX64Pe {
    param(
        [Parameter(Mandatory)][object] $Entry,
        [Parameter(Mandatory)][string] $RelativePath
    )

    $prefix = $Entry.prefix
    if ($Entry.length -lt 64 -or $null -eq $prefix -or $prefix.Length -lt 64 -or
        $prefix[0] -ne 0x4d -or $prefix[1] -ne 0x5a) {
        throw "Archive executable '$RelativePath' is not a Windows x64 PE executable."
    }
    $peOffset = [System.BitConverter]::ToInt32($prefix, 60)
    if ($peOffset -lt 64 -or $peOffset -gt ($prefix.Length - 26) -or
        $prefix[$peOffset] -ne 0x50 -or $prefix[$peOffset + 1] -ne 0x45 -or
        $prefix[$peOffset + 2] -ne 0x00 -or $prefix[$peOffset + 3] -ne 0x00 -or
        [System.BitConverter]::ToUInt16($prefix, $peOffset + 4) -ne 0x8664 -or
        [System.BitConverter]::ToUInt16($prefix, $peOffset + 24) -ne 0x20b) {
        throw "Archive executable '$RelativePath' is not a PE32+ AMD64 executable."
    }
}

function Get-ExpectedPaths {
    param([Parameter(Mandatory)][string] $PackageName)

    return @(
        "$PackageName/RelayCove.Client.exe",
        "$PackageName/RelayCove.Updater.exe",
        "$PackageName/RelayCove.Client.dll",
        "$PackageName/RelayCove.Client.deps.json",
        "$PackageName/RelayCove.Client.runtimeconfig.json",
        "$PackageName/hostfxr.dll",
        "$PackageName/hostpolicy.dll",
        "$PackageName/coreclr.dll",
        "$PackageName/Microsoft.WindowsAppRuntime.dll",
        "$PackageName/Microsoft.WindowsAppRuntime.Bootstrap.dll",
        "$PackageName/Microsoft.UI.Xaml.Controls.dll",
        "$PackageName/Microsoft.ui.xaml.dll",
        "$PackageName/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll",
        "$PackageName/WinRT.Runtime.dll",
        "$PackageName/e_sqlite3.dll",
        "$PackageName/manifest.json"
    )
}

function Get-ReleaseSummary {
    param(
        [Parameter(Mandatory)][string] $ResolvedOutputRoot,
        [Parameter(Mandatory)][string] $RequestedVersion,
        [Parameter(Mandatory)][string] $RequiredCommit,
        [switch] $PermitDirtySource
    )

    $packageName = "$archivePrefix-$RequestedVersion-$runtimeIdentifier"
    $container = Join-Path (Join-Path $ResolvedOutputRoot "client") $RequestedVersion
    $archiveFileName = "$packageName.zip"
    $archivePath = Join-Path $container $archiveFileName
    $sidecarPath = "$archivePath.sha256"
    Assert-NoReparsePointAncestors $container $artifactsRoot
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or -not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
        throw "Release archive and sidecar are required under $container."
    }
    Assert-NoReparsePointAncestors $archivePath $artifactsRoot
    Assert-NoReparsePointAncestors $sidecarPath $artifactsRoot
    if ((Get-Item -LiteralPath $sidecarPath -Force).Length -gt 1024) { throw "SHA-256 sidecar exceeds the 1024-byte safety limit." }
    $sidecar = [System.IO.File]::ReadAllText($sidecarPath, [System.Text.UTF8Encoding]::new($false)).TrimEnd("`r", "`n")
    $expectedSidecar = "\A([0-9a-f]{64})  " + [System.Text.RegularExpressions.Regex]::Escape($archiveFileName) + '\z'
    if ($sidecar -notmatch $expectedSidecar) { throw "SHA-256 sidecar has an unexpected format." }
    $actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $Matches[1]) { throw "SHA-256 sidecar does not match the release archive." }

    $archive = Read-Archive $archivePath $packageName
    $entryMap = @{}
    foreach ($entry in $archive.Entries) { $entryMap[$entry.name] = $entry }
    foreach ($expectedPath in Get-ExpectedPaths $packageName) {
        if (-not $entryMap.ContainsKey($expectedPath)) { throw "Archive is missing required entry '$expectedPath'." }
    }
    Assert-SelfContainedRuntimeConfiguration $archive.TextEntries["$packageName/RelayCove.Client.runtimeconfig.json"]
    Assert-ArchiveOrdering $archive.Entries
    Assert-WindowsX64Pe $entryMap["$packageName/RelayCove.Client.exe"] "RelayCove.Client.exe"
    $updaterEntry = $entryMap["$packageName/RelayCove.Updater.exe"]
    if ($updaterEntry.length -lt 1MB -or $updaterEntry.length -gt 1GB) {
        throw "Archive updater must be a standalone executable between 1 MiB and 1 GiB."
    }
    Assert-WindowsX64Pe $updaterEntry "RelayCove.Updater.exe"

    foreach ($entry in $archive.Entries) {
        $relativePath = $entry.name.Substring($packageName.Length).Trim('/')
        if ($entry.isDirectory) {
            if ($relativePath -ne "") { throw "Archive contains an unexpected directory '$relativePath'." }
            continue
        }
        $fileName = [System.IO.Path]::GetFileName($relativePath)
        if ($fileName.StartsWith("RelayCove.Updater.", [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $relativePath.Equals("RelayCove.Updater.exe", [System.StringComparison]::Ordinal)) {
            throw "Archive contains forbidden updater companion '$relativePath'."
        }
        if ($relativePath -ne "manifest.json" -and (Test-ForbiddenPath $relativePath)) {
            throw "Archive contains forbidden file '$relativePath'."
        }
    }

    $manifest = $archive.ManifestJson | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -ne $RequestedVersion -or
        $manifest.rid -ne $runtimeIdentifier -or $manifest.selfContained -ne $true -or
        $manifest.windowsAppSdkSelfContained -ne $true -or
        $manifest.packageRoot -ne $packageName -or [string]::IsNullOrWhiteSpace($manifest.sdkVersion) -or
        $manifest.selfContained -isnot [bool] -or $manifest.windowsAppSdkSelfContained -isnot [bool] -or
        $manifest.sourceTreeClean -isnot [bool] -or
        $manifest.commit -notmatch '\A[0-9a-f]{40}\z' -or $manifest.commit -ne $RequiredCommit) {
        throw "manifest.json has invalid release metadata."
    }
    if ($manifest.sourceTreeClean -ne $true -and -not $PermitDirtySource) {
        throw "manifest.json records a dirty source tree; use -AllowDirtySource only for local validation."
    }
    $manifestFiles = @($manifest.files)
    $expectedArchiveFiles = @($archive.Entries | Where-Object { -not $_.isDirectory -and $_.name -ne "$packageName/manifest.json" })
    if ($manifestFiles.Count -ne $expectedArchiveFiles.Count) { throw "manifest.json file count does not match archive content." }
    $previousPath = $null
    $seenManifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($manifestFile in $manifestFiles) {
        if ($manifestFile.path -isnot [string] -or [string]::IsNullOrWhiteSpace($manifestFile.path) -or
            $manifestFile.path.Contains('\', [System.StringComparison]::Ordinal) -or
            $manifestFile.path -match '(^|/)\.\.(/|$)' -or [System.IO.Path]::IsPathFullyQualified($manifestFile.path) -or
            -not $seenManifestPaths.Add([string]$manifestFile.path)) { throw "manifest.json contains an unsafe path." }
        if ($null -ne $previousPath -and [string]::CompareOrdinal($previousPath, [string]$manifestFile.path) -ge 0) {
            throw "manifest.json files are not strictly ordinal sorted."
        }
        $previousPath = [string]$manifestFile.path
        $archiveEntry = $entryMap["$packageName/$($manifestFile.path)"]
        if ($manifestFile.attributes -ne "00000080" -or
            $null -eq $archiveEntry -or $archiveEntry.length -ne [int64]$manifestFile.length -or
            $archiveEntry.sha256 -ne $manifestFile.sha256) { throw "manifest.json does not match archive entry '$($manifestFile.path)'." }
    }
    return [pscustomobject]@{ Container = $container; ArchivePath = $archivePath; ArchiveSha256 = $actualArchiveHash; Manifest = $manifest }
}

function Compare-Releases {
    param([Parameter(Mandatory)][object] $Left, [Parameter(Mandatory)][object] $Right)

    foreach ($property in @("schemaVersion", "version", "commit", "sourceTreeClean", "rid", "selfContained", "windowsAppSdkSelfContained", "sdkVersion", "packageRoot")) {
        if ($Left.Manifest[$property] -ne $Right.Manifest[$property]) { throw "Release manifests differ at '$property'." }
    }
    $leftFiles = @($Left.Manifest.files); $rightFiles = @($Right.Manifest.files)
    if ($leftFiles.Count -ne $rightFiles.Count) { throw "Release manifests have different file counts." }
    for ($index = 0; $index -lt $leftFiles.Count; $index++) {
        $leftFile = $leftFiles[$index]; $rightFile = $rightFiles[$index]
        if ($leftFile.path -ne $rightFile.path -or $leftFile.length -ne $rightFile.length -or
            $leftFile.sha256 -ne $rightFile.sha256 -or $leftFile.attributes -ne $rightFile.attributes) {
            throw "Release manifests differ at file index $index."
        }
    }
    if ($Left.ArchiveSha256 -ne $Right.ArchiveSha256) { throw "Repeated archive hashes differ despite identical manifest files." }
    Write-Host "Repeated archive comparison passed: byte-identical archive SHA-256."
}

Assert-ReleaseVersion $Version
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git -C $repositoryRoot rev-parse --verify HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine the expected Git commit."
    }
}
if ($ExpectedCommit -notmatch '\A[0-9a-f]{40}\z') {
    throw "ExpectedCommit must be exactly 40 lowercase hexadecimal characters."
}
$resolvedOutputRoot = Resolve-PathInside $OutputRoot $artifactsRoot "OutputRoot" -AllowRoot
Assert-NoReparsePointAncestors $resolvedOutputRoot $artifactsRoot
$primary = Get-ReleaseSummary $resolvedOutputRoot $Version $ExpectedCommit -PermitDirtySource:$AllowDirtySource
if (-not [string]::IsNullOrWhiteSpace($CompareOutputRoot)) {
    $resolvedCompareOutputRoot = Resolve-PathInside $CompareOutputRoot $artifactsRoot "CompareOutputRoot" -AllowRoot
    if ($resolvedCompareOutputRoot.Equals($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "CompareOutputRoot must identify a distinct release build root." }
    Assert-NoReparsePointAncestors $resolvedCompareOutputRoot $artifactsRoot
    $comparison = Get-ReleaseSummary $resolvedCompareOutputRoot $Version $ExpectedCommit -PermitDirtySource:$AllowDirtySource
    Compare-Releases $primary $comparison
}
Write-Host "RelayCove Client release verification passed: $($primary.ArchivePath)"
