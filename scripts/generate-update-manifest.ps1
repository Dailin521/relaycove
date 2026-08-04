[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $MinimumSupportedVersion,

    [Parameter(Mandatory)]
    [string] $DownloadUrl,

    [switch] $Mandatory,

    [string] $ReleaseNotes = "",

    [string] $ClientReleaseRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"),

    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"),

    [string] $ExpectedCommit,

    [switch] $AllowDirtySource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$verifyScript = Join-Path $PSScriptRoot "verify-client-release.ps1"
$archivePrefix = "RelayCove.Client"
$runtimeIdentifier = "win-x64"
$maximumArtifactBytes = 2L * 1024 * 1024 * 1024
$maximumArtifactUrlLength = 2048
$maximumReleaseNotesLength = 8192

function Assert-StrictSemanticVersion {
    param([Parameter(Mandatory)][string] $Value, [Parameter(Mandatory)][string] $Description)

    if ($Value.Length -gt 64 -or $Value -notmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?\z') {
        throw "$Description must be strict SemVer major.minor.patch with an optional prerelease and no build metadata."
    }
}

function Compare-NumericIdentifier {
    param([Parameter(Mandatory)][string] $Left, [Parameter(Mandatory)][string] $Right)

    if ($Left.Length -ne $Right.Length) {
        return [Math]::Sign($Left.Length - $Right.Length)
    }
    return [string]::CompareOrdinal($Left, $Right)
}

function Compare-SemanticVersion {
    param([Parameter(Mandatory)][string] $Left, [Parameter(Mandatory)][string] $Right)

    $leftParts = $Left.Split('-', 2)
    $rightParts = $Right.Split('-', 2)
    $leftCore = $leftParts[0].Split('.')
    $rightCore = $rightParts[0].Split('.')
    for ($index = 0; $index -lt 3; $index++) {
        $comparison = Compare-NumericIdentifier $leftCore[$index] $rightCore[$index]
        if ($comparison -ne 0) {
            return $comparison
        }
    }

    $leftHasPrerelease = $leftParts.Length -eq 2
    $rightHasPrerelease = $rightParts.Length -eq 2
    if (-not $leftHasPrerelease -and -not $rightHasPrerelease) { return 0 }
    if (-not $leftHasPrerelease) { return 1 }
    if (-not $rightHasPrerelease) { return -1 }

    $leftIdentifiers = $leftParts[1].Split('.')
    $rightIdentifiers = $rightParts[1].Split('.')
    $length = [Math]::Min($leftIdentifiers.Length, $rightIdentifiers.Length)
    for ($index = 0; $index -lt $length; $index++) {
        $leftIdentifier = $leftIdentifiers[$index]
        $rightIdentifier = $rightIdentifiers[$index]
        $leftNumeric = $leftIdentifier -match '\A[0-9]+\z'
        $rightNumeric = $rightIdentifier -match '\A[0-9]+\z'
        if ($leftNumeric -and $rightNumeric) {
            $comparison = Compare-NumericIdentifier $leftIdentifier $rightIdentifier
        }
        elseif ($leftNumeric) {
            $comparison = -1
        }
        elseif ($rightNumeric) {
            $comparison = 1
        }
        else {
            $comparison = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
        }
        if ($comparison -ne 0) {
            return $comparison
        }
    }

    return [Math]::Sign($leftIdentifiers.Length - $rightIdentifiers.Length)
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

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Artifacts root does not exist: $Root"
    }

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Update manifest paths must not use a reparse-point artifacts root: $Root"
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
            throw "Update manifest paths must not traverse a reparse point: $currentPath"
        }
    }
}

function Get-CurrentGitCommit {
    $commit = & git -C $repositoryRoot rev-parse --verify HEAD
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        throw "Unable to determine the expected Git commit; git exited with code $gitExitCode."
    }

    return ($commit | Out-String).Trim()
}

function Write-Atomically {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][byte[]] $Bytes)

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporaryPath = Join-Path $directory ("." + [System.Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        [System.IO.File]::WriteAllBytes($temporaryPath, $Bytes)
        [System.IO.File]::Move($temporaryPath, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Assert-StrictSemanticVersion $Version "Version"
Assert-StrictSemanticVersion $MinimumSupportedVersion "MinimumSupportedVersion"
if ((Compare-SemanticVersion $Version $MinimumSupportedVersion) -lt 0) {
    throw "Version must not be below MinimumSupportedVersion."
}
if ($ReleaseNotes.Length -gt $maximumReleaseNotesLength) {
    throw "ReleaseNotes exceeds the $maximumReleaseNotesLength character limit."
}
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = Get-CurrentGitCommit
}
if ($ExpectedCommit -cnotmatch '\A[0-9a-f]{40}\z') {
    throw "ExpectedCommit must be exactly 40 lowercase hexadecimal characters."
}
if ($DownloadUrl.Length -gt $maximumArtifactUrlLength) {
    throw "DownloadUrl exceeds the $maximumArtifactUrlLength character limit."
}
$downloadUri = $null
if (-not [Uri]::TryCreate($DownloadUrl, [UriKind]::Absolute, [ref] $downloadUri) -or
    -not $downloadUri.Scheme.Equals("https", [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::IsNullOrEmpty($downloadUri.Host) -or -not [string]::IsNullOrEmpty($downloadUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($downloadUri.Fragment)) {
    throw "DownloadUrl must be an absolute HTTPS URL without user info or fragment."
}

$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
$resolvedClientReleaseRoot = Resolve-PathInside $ClientReleaseRoot $resolvedArtifactsRoot "ClientReleaseRoot" -AllowRoot
$resolvedOutputRoot = Resolve-PathInside $OutputRoot $resolvedArtifactsRoot "OutputRoot" -AllowRoot
Assert-NoReparsePointAncestors $resolvedClientReleaseRoot $resolvedArtifactsRoot
Assert-NoReparsePointAncestors $resolvedOutputRoot $resolvedArtifactsRoot

$packageName = "$archivePrefix-$Version-$runtimeIdentifier"
$releaseContainer = Join-Path (Join-Path $resolvedClientReleaseRoot "client") $Version
$archiveFileName = "$packageName.zip"
$archivePath = Join-Path $releaseContainer $archiveFileName
Assert-NoReparsePointAncestors $archivePath $resolvedArtifactsRoot
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Client release archive was not found: $archivePath"
}
$archiveInfo = Get-Item -LiteralPath $archivePath -Force
if ($archiveInfo.Length -lt 1 -or $archiveInfo.Length -gt $maximumArtifactBytes) {
    throw "Client release archive size is outside the supported range."
}

$verifyParameters = @{
    Version = $Version
    OutputRoot = $resolvedClientReleaseRoot
    ExpectedCommit = $ExpectedCommit
}
if ($AllowDirtySource) {
    $verifyParameters.AllowDirtySource = $true
}
& $verifyScript @verifyParameters
$verifySucceeded = $?
if (-not $verifySucceeded) {
    throw "Client release verification failed."
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    channel = "internal-rc"
    version = $Version
    minimumSupportedVersion = $MinimumSupportedVersion
    mandatory = [bool]$Mandatory
    artifact = [ordered]@{
        type = "portable-zip"
        url = $downloadUri.AbsoluteUri
        sizeBytes = [int64]$archiveInfo.Length
        sha256 = $archiveHash
    }
    releaseNotes = $ReleaseNotes
}
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
    ($manifest | ConvertTo-Json -Compress) + "`n")
$outputDirectory = Join-Path (Join-Path (Join-Path $resolvedOutputRoot "updates") "internal-rc") $Version
$outputPath = Join-Path $outputDirectory "manifest.json"
Assert-NoReparsePointAncestors $outputPath $resolvedArtifactsRoot
if (Test-Path -LiteralPath $outputPath) {
    throw "Update manifest already exists and will not be overwritten: $outputPath"
}
Write-Atomically $outputPath $manifestBytes
Write-Host "RelayCove update manifest created: $outputPath"
