[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"),

    [string] $CompareOutputRoot,

    [switch] $AllowDirtySource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$runtimeIdentifier = "linux-x64"
$archivePrefix = "RelayCove.Server"
$migrationBundleName = "RelayCove.Migrations"
$maximumTextEntryBytes = 1MB
$maximumArchiveEntries = 2048
$maximumUncompressedBytes = 1GB

function Assert-ReleaseVersion {
    param([Parameter(Mandatory)][string] $Value)

    if ($Value -notmatch '\A[0-9A-Za-z](?:[0-9A-Za-z.-]{0,63})\z' -or
        $Value.Contains("..", [System.StringComparison]::Ordinal)) {
        throw "Version must be 1-64 ASCII letters, digits, dots, or hyphens without '..'."
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

function Get-ExpectedPaths {
    param([Parameter(Mandatory)][string] $PackageName)

    return @(
        "$PackageName/",
        "$PackageName/app/",
        "$PackageName/deploy/",
        "$PackageName/migrate/",
        "$PackageName/app/RelayCove.Server",
        "$PackageName/app/RelayCove.Server.deps.json",
        "$PackageName/app/RelayCove.Server.runtimeconfig.json",
        "$PackageName/app/libhostfxr.so",
        "$PackageName/app/libhostpolicy.so",
        "$PackageName/app/libcoreclr.so",
        "$PackageName/migrate/$migrationBundleName",
        "$PackageName/deploy/relaycove.service",
        "$PackageName/deploy/nginx.conf",
        "$PackageName/deploy/appsettings.Production.example.json",
        "$PackageName/deploy/relaycove.env.example",
        "$PackageName/deploy/DEPLOYMENT.md",
        "$PackageName/manifest.json"
    )
}

function Get-ExpectedMode {
    param([Parameter(Mandatory)][string] $RelativePath)

    return $RelativePath -in @(
        "app/RelayCove.Server",
        "migrate/$migrationBundleName"
    ) ? "0755" : "0644"
}

function Format-LinuxMode {
    param([Parameter(Mandatory)][int] $Mode)

    return [System.Convert]::ToString($Mode, 8).PadLeft(4, '0')
}

function Test-ForbiddenPath {
    param([Parameter(Mandatory)][string] $RelativePath)

    return $RelativePath -match '(?i)(^|/)(bin|obj|uploads|logs)(/|$)|\.pdb$|\.cs$|\.csproj$|\.sln$|(^|/)appsettings\.Development\.json$|(^|/).*\.(db|sqlite)(-wal|-shm)?$|(^|/)\.env(?:\.|$)|(^|/)[^/]*secret[^/]*\.json$|\.(pfx|p12|pem|key|user|bak|tmp)$'
}

function Get-StreamInspection {
    param([Parameter(Mandatory)][System.IO.Stream] $Stream)

    $sha256 = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(81920)
        $prefix = [byte[]]::new(20)
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

function Read-Archive {
    param(
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $PackageName
    )

    $archiveStream = $null
    $gzipStream = $null
    $reader = $null
    try {
        $archiveStream = [System.IO.File]::OpenRead($ArchivePath)
        $gzipStream = [System.IO.Compression.GZipStream]::new(
            $archiveStream,
            [System.IO.Compression.CompressionMode]::Decompress,
            $false)
        $reader = [System.Formats.Tar.TarReader]::new($gzipStream, $false)
        $seen = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        $entries = [System.Collections.Generic.List[object]]::new()
        $textEntries = @{}
        $manifestJson = $null
        [long] $totalLength = 0

        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $entryName = $entry.Name
            $pathSegments = $entryName.Split('/')
            $hasUnsafeSegment = $false
            for ($segmentIndex = 0; $segmentIndex -lt $pathSegments.Length; $segmentIndex++) {
                $segment = $pathSegments[$segmentIndex]
                $isPermittedTrailingDirectorySeparator =
                    $segmentIndex -eq ($pathSegments.Length - 1) -and
                    [string]::IsNullOrEmpty($segment) -and
                    $entryName.EndsWith('/', [System.StringComparison]::Ordinal)
                if (($segment -eq "." -or $segment -eq ".." -or [string]::IsNullOrEmpty($segment)) -and
                    -not $isPermittedTrailingDirectorySeparator) {
                    $hasUnsafeSegment = $true
                    break
                }
            }
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                $entryName.Contains('\', [System.StringComparison]::Ordinal) -or
                [System.IO.Path]::IsPathFullyQualified($entryName) -or
                $hasUnsafeSegment -or
                ($entryName -ne "$PackageName/" -and -not $entryName.StartsWith("$PackageName/", [System.StringComparison]::Ordinal)) -or
                -not $seen.Add($entryName)) {
                throw "Archive contains an unsafe or duplicate entry '$entryName'."
            }
            if ($entries.Count -ge $maximumArchiveEntries) {
                throw "Archive exceeds the maximum entry count of $maximumArchiveEntries."
            }

            if ($entry.EntryType -notin @(
                [System.Formats.Tar.TarEntryType]::Directory,
                [System.Formats.Tar.TarEntryType]::RegularFile)) {
                throw "Archive contains unsupported entry type '$($entry.EntryType)' for '$entryName'."
            }
            if ($entry.Format -ne [System.Formats.Tar.TarEntryFormat]::Ustar -or
                $entry.ModificationTime -ne [System.DateTimeOffset]::UnixEpoch -or
                $entry.Uid -ne 0 -or
                $entry.Gid -ne 0 -or
                -not [string]::IsNullOrEmpty($entry.UserName) -or
                -not [string]::IsNullOrEmpty($entry.GroupName)) {
                throw "Archive entry '$entryName' has non-deterministic or unsupported metadata."
            }

            $isDirectory = $entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory
            if ($isDirectory) {
                if (-not $entryName.EndsWith('/', [System.StringComparison]::Ordinal)) {
                    throw "Archive directory entry '$entryName' does not end in '/'."
                }
                if ($entry.Mode -ne 493) {
                    throw "Archive directory '$entryName' does not have mode 0755."
                }
                $entries.Add([pscustomobject]@{
                        name = $entryName
                        isDirectory = $true
                        length = 0
                        sha256 = $null
                        prefix = $null
                        mode = "0755"
                    })
                continue
            }

            if ($null -eq $entry.DataStream) {
                throw "Archive file entry '$entryName' has no content stream."
            }

            if ($entry.Length -lt 0 -or $entry.Length -gt ($maximumUncompressedBytes - $totalLength)) {
                throw "Archive exceeds the maximum uncompressed size of $maximumUncompressedBytes bytes."
            }
            $totalLength += $entry.Length

            if ($entryName -eq "$PackageName/manifest.json" -or
                $entryName -eq "$PackageName/app/appsettings.json" -or
                $entryName.StartsWith("$PackageName/deploy/", [System.StringComparison]::Ordinal)) {
                if ($entry.Length -gt $maximumTextEntryBytes) {
                    throw "Archive text entry '$entryName' exceeds $maximumTextEntryBytes bytes."
                }
                $bytes = [byte[]]::new([int]$entry.Length)
                $offset = 0
                while ($offset -lt $bytes.Length) {
                    $bytesRead = $entry.DataStream.Read($bytes, $offset, $bytes.Length - $offset)
                    if ($bytesRead -eq 0) {
                        throw "Archive text entry '$entryName' ended before its declared length."
                    }
                    $offset += $bytesRead
                }
                try {
                    $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
                }
                catch [System.Text.DecoderFallbackException] {
                    throw "Archive text entry '$entryName' is not valid UTF-8."
                }
                if ($entryName -eq "$PackageName/manifest.json") {
                    $manifestJson = $text
                }
                else {
                    $textEntries[$entryName] = $text
                }
                $sha256 = [System.Security.Cryptography.SHA256]::HashData($bytes)
                $hash = ([System.Convert]::ToHexString($sha256)).ToLowerInvariant()
                $length = $bytes.LongLength
                $prefixLength = [Math]::Min($bytes.Length, 20)
                $prefix = [byte[]]::new($prefixLength)
                [System.Array]::Copy($bytes, $prefix, $prefixLength)
            }
            else {
                $inspection = Get-StreamInspection $entry.DataStream
                if ($inspection.Length -ne $entry.Length) {
                    throw "Archive entry '$entryName' ended before or after its declared length."
                }
                $length = $inspection.Length
                $hash = $inspection.Hash
                $prefix = $inspection.Prefix
            }

            $entries.Add([pscustomobject]@{
                    name = $entryName
                    isDirectory = $false
                    length = $length
                    sha256 = $hash
                    prefix = $prefix
                    mode = (Format-LinuxMode $entry.Mode)
                })
        }

        if ($null -eq $manifestJson) {
            throw "Archive does not contain manifest.json."
        }

        return [pscustomobject]@{
            Entries = @($entries)
            ManifestJson = $manifestJson
            TextEntries = $textEntries
        }
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $gzipStream) {
            $gzipStream.Dispose()
        }
        elseif ($null -ne $archiveStream) {
            $archiveStream.Dispose()
        }
    }
}

function Assert-EnvironmentExample {
    param([Parameter(Mandatory)][string] $Content)

    $allowedAssignments = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
    $allowedAssignments.Add("Authentication__SigningKey", "REPLACE_WITH_BASE64_OF_AT_LEAST_32_RANDOM_BYTES")
    $allowedAssignments.Add("BootstrapAdmin__Enabled", "false")
    $allowedAssignments.Add("BootstrapAdmin__UserName", "REPLACE_WITH_INITIAL_ADMIN_USERNAME")
    $allowedAssignments.Add("BootstrapAdmin__DisplayName", "REPLACE_WITH_INITIAL_ADMIN_DISPLAY_NAME")
    $allowedAssignments.Add("BootstrapAdmin__Password", "REPLACE_WITH_INITIAL_ADMIN_PASSWORD")

    $seenAssignments = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $lineNumber = 0
    foreach ($line in ($Content -split "`r?`n")) {
        $lineNumber++
        $trimmedLine = $line.Trim().TrimStart([char]0xFEFF)
        if ([string]::IsNullOrWhiteSpace($trimmedLine) -or $trimmedLine.StartsWith("#", [System.StringComparison]::Ordinal)) {
            continue
        }
        if ($trimmedLine -notmatch '^(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$') {
            throw "Deployment environment example has an invalid active assignment on line $lineNumber."
        }

        $key = $Matches.key
        $value = $Matches.value
        if ($key -eq "ASPNETCORE_URLS") {
            throw "Deployment environment example must not set ASPNETCORE_URLS."
        }
        if ($key -match '^(?i:ConnectionStrings?|Database)') {
            throw "Deployment environment example must not contain a connection string."
        }
        if (-not $allowedAssignments.ContainsKey($key)) {
            throw "Deployment environment example contains an unapproved active setting '$key'."
        }
        if (-not $seenAssignments.Add($key)) {
            throw "Deployment environment example contains a duplicate active setting '$key'."
        }
        if ($value -ne $allowedAssignments[$key]) {
            throw "Deployment environment example setting '$key' must use its documented placeholder or safe value."
        }
    }

    if (-not $seenAssignments.Contains("Authentication__SigningKey")) {
        throw "Deployment environment example must provide the Authentication__SigningKey placeholder."
    }
    $bootstrapCredentialKeys = @(
        "BootstrapAdmin__UserName",
        "BootstrapAdmin__DisplayName",
        "BootstrapAdmin__Password")
    if (@($bootstrapCredentialKeys | Where-Object { $seenAssignments.Contains($_) }).Count -gt 0 -and
        -not $seenAssignments.Contains("BootstrapAdmin__Enabled")) {
        throw "Deployment environment example may include bootstrap placeholders only with BootstrapAdmin__Enabled=false."
    }
}

function Assert-ProductionConfiguration {
    param(
        [Parameter(Mandatory)][hashtable] $Configuration,
        [Parameter(Mandatory)][string] $PackageVersion
    )

    if (-not $Configuration.ContainsKey("ConnectionStrings") -or
        -not $Configuration.ContainsKey("Storage") -or
        -not $Configuration.ContainsKey("Uploads") -or
        -not $Configuration.ContainsKey("Authentication") -or
        -not $Configuration.ContainsKey("BootstrapAdmin") -or
        $Configuration.ConnectionStrings.Default -ne "Data Source=/var/lib/relaycove/relaycove.db;Foreign Keys=True;Default Timeout=5" -or
        $Configuration.Storage.UploadsPath -ne "/var/lib/relaycove/uploads" -or
        $Configuration.Uploads.MaximumFileBytes -ne 104857600 -or
        $Configuration.BootstrapAdmin.Enabled -ne $false -or
        $Configuration.BootstrapAdmin.ContainsKey("Password") -or
        $Configuration.BootstrapAdmin.ContainsKey("UserName") -or
        $Configuration.BootstrapAdmin.ContainsKey("DisplayName") -or
        $Configuration.Authentication.ServerVersion -ne $PackageVersion -or
        $Configuration.Authentication.ContainsKey("SigningKey")) {
        throw "Production configuration example does not match the current server configuration boundary."
    }
}

function Assert-NoSensitiveConfiguration {
    param(
        [AllowNull()][object] $Value,
        [string] $Path = "root"
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $keyName = [string] $key
            $childPath = "$Path.$keyName"
            if ($keyName -match '(?i)(Password|SigningKey|ApiKey|ClientSecret|PrivateKey|AccessToken|RefreshToken|Secret|Token)$') {
                throw "Packaged application settings contain a sensitive key at '$childPath'."
            }
            Assert-NoSensitiveConfiguration $Value[$key] $childPath
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-NoSensitiveConfiguration $item "$Path[$index]"
            $index++
        }
        return
    }

    if ($Value -is [string]) {
        if ($Value -match '(?i)-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----') {
            throw "Packaged application settings contain a private-key value at '$Path'."
        }
        if ($Value -match '(?i)(?:^|;)\s*(?:Password|Pwd|User ID)\s*=') {
            throw "Packaged application settings contain a credential-bearing connection string at '$Path'."
        }
    }
}

function Assert-LinuxX64Elf {
    param(
        [Parameter(Mandatory)][object] $Entry,
        [Parameter(Mandatory)][string] $RelativePath
    )

    if ($Entry.length -lt 20 -or $null -eq $Entry.prefix -or $Entry.prefix.Length -lt 20 -or
        $Entry.prefix[0] -ne 0x7f -or
        $Entry.prefix[1] -ne 0x45 -or
        $Entry.prefix[2] -ne 0x4c -or
        $Entry.prefix[3] -ne 0x46 -or
        $Entry.prefix[4] -ne 0x02 -or
        $Entry.prefix[5] -ne 0x01 -or
        $Entry.prefix[18] -ne 0x3e -or
        $Entry.prefix[19] -ne 0x00) {
        throw "Archive executable '$RelativePath' is not a non-empty Linux x86-64 ELF binary."
    }
}

function Assert-DeployMaterials {
    param(
        [Parameter(Mandatory)][hashtable] $TextEntries,
        [Parameter(Mandatory)][string] $PackageName,
        [Parameter(Mandatory)][string] $Version
    )

    Assert-EnvironmentExample $TextEntries["$PackageName/deploy/relaycove.env.example"]
    $productionConfig = $TextEntries["$PackageName/deploy/appsettings.Production.example.json"] | ConvertFrom-Json -AsHashtable
    Assert-ProductionConfiguration $productionConfig $Version

    $systemd = $TextEntries["$PackageName/deploy/relaycove.service"]
    foreach ($requiredLine in @(
        "User=relaycove",
        "Group=relaycove",
        "ExecStart=/opt/relaycove/current/app/RelayCove.Server",
        "EnvironmentFile=/etc/relaycove/relaycove.env",
        "StateDirectory=relaycove",
        "UMask=0077",
        "ReadWritePaths=/var/lib/relaycove")) {
        if (-not $systemd.Contains($requiredLine, [System.StringComparison]::Ordinal)) {
            throw "systemd example is missing '$requiredLine'."
        }
    }

    $nginx = $TextEntries["$PackageName/deploy/nginx.conf"]
    foreach ($requiredLine in @(
        "server 127.0.0.1:5080;",
        "client_max_body_size 102464k;",
        "location /hubs/chat",
        'proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;',
        'proxy_set_header X-Forwarded-Proto $scheme;',
        'proxy_set_header Upgrade $http_upgrade;',
        'proxy_set_header Connection $relaycove_connection_upgrade;')) {
        if (-not $nginx.Contains($requiredLine, [System.StringComparison]::Ordinal)) {
            throw "Nginx example is missing '$requiredLine'."
        }
    }
}

function Get-ReleaseSummary {
    param(
        [Parameter(Mandatory)][string] $ResolvedOutputRoot,
        [Parameter(Mandatory)][string] $RequestedVersion,
        [switch] $PermitDirtySource
    )

    $packageName = "$archivePrefix-$RequestedVersion-$runtimeIdentifier"
    $container = Join-Path (Join-Path $ResolvedOutputRoot "server") $RequestedVersion
    $archiveFileName = "$packageName.tar.gz"
    $archivePath = Join-Path $container $archiveFileName
    $sidecarPath = "$archivePath.sha256"
    Assert-NoReparsePointAncestors $container $artifactsRoot
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
        throw "Release archive and sidecar are required under $container."
    }
    Assert-NoReparsePointAncestors $archivePath $artifactsRoot
    Assert-NoReparsePointAncestors $sidecarPath $artifactsRoot
    if ((Get-Item -LiteralPath $sidecarPath -Force).Length -gt 1024) {
        throw "SHA-256 sidecar exceeds the 1024-byte safety limit."
    }

    $sidecar = [System.IO.File]::ReadAllText($sidecarPath, [System.Text.UTF8Encoding]::new($false)).TrimEnd("`r", "`n")
    $expectedSidecar = "\A([0-9a-f]{64})  " + [System.Text.RegularExpressions.Regex]::Escape($archiveFileName) + '\z'
    if ($sidecar -notmatch $expectedSidecar) {
        throw "SHA-256 sidecar has an unexpected format."
    }
    $actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $Matches[1]) {
        throw "SHA-256 sidecar does not match the release archive."
    }

    $archive = Read-Archive $archivePath $packageName
    $entryMap = @{}
    foreach ($entry in $archive.Entries) {
        $entryMap[$entry.name] = $entry
    }
    foreach ($expectedPath in Get-ExpectedPaths $packageName) {
        if (-not $entryMap.ContainsKey($expectedPath)) {
            throw "Archive is missing required entry '$expectedPath'."
        }
    }
    foreach ($elfPath in @(
        "app/RelayCove.Server",
        "app/libhostfxr.so",
        "app/libhostpolicy.so",
        "app/libcoreclr.so",
        "migrate/$migrationBundleName")) {
        Assert-LinuxX64Elf $entryMap["$packageName/$elfPath"] $elfPath
    }

    foreach ($entry in $archive.Entries) {
        $relativePath = $entry.name.Substring($packageName.Length).Trim('/')
        if ($entry.isDirectory) {
            if ($relativePath -notin @("", "app", "deploy", "migrate")) {
                throw "Archive contains an unexpected directory '$relativePath'."
            }
            continue
        }
        if ($relativePath -eq "manifest.json") {
            continue
        }
        if (($relativePath -notmatch '^app/' -and
             $relativePath -notin @(
                "migrate/$migrationBundleName",
                "deploy/relaycove.service",
                "deploy/nginx.conf",
                "deploy/appsettings.Production.example.json",
                "deploy/relaycove.env.example",
                "deploy/DEPLOYMENT.md")) -or
            (Test-ForbiddenPath $relativePath) -or
            $relativePath -match '(?i)^app/appsettings\.(?!json$)') {
            throw "Archive contains forbidden file '$relativePath'."
        }
        $expectedMode = Get-ExpectedMode $relativePath
        if ($entry.mode -ne $expectedMode) {
            throw "Archive file '$relativePath' has mode $($entry.mode), expected $expectedMode."
        }
    }

    $applicationSettings = $archive.TextEntries["$packageName/app/appsettings.json"] | ConvertFrom-Json -AsHashtable
    if ($applicationSettings.Authentication.ContainsKey("SigningKey") -or
        ($applicationSettings.ContainsKey("BootstrapAdmin") -and
            ($applicationSettings.BootstrapAdmin.ContainsKey("Password") -or
             $applicationSettings.BootstrapAdmin.ContainsKey("UserName") -or
             $applicationSettings.BootstrapAdmin.ContainsKey("DisplayName")))) {
        throw "Packaged application settings contain authentication or bootstrap credentials."
    }
    Assert-NoSensitiveConfiguration $applicationSettings
    Assert-DeployMaterials $archive.TextEntries $packageName $RequestedVersion

    $manifest = $archive.ManifestJson | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.version -ne $RequestedVersion -or
        $manifest.rid -ne $runtimeIdentifier -or
        $manifest.selfContained -ne $true -or
        $manifest.packageRoot -ne $packageName -or
        [string]::IsNullOrWhiteSpace($manifest.sdkVersion) -or
        $manifest.selfContained -isnot [bool] -or
        $manifest.sourceTreeClean -isnot [bool] -or
        $manifest.commit -notmatch '\A[0-9a-f]{40}\z') {
        throw "manifest.json has invalid release metadata."
    }
    if ($manifest.sourceTreeClean -ne $true -and -not $PermitDirtySource) {
        throw "manifest.json records a dirty source tree; use -AllowDirtySource only for local validation."
    }

    $manifestFiles = @($manifest.files)
    $expectedArchiveFiles = @(
        $archive.Entries | Where-Object { -not $_.isDirectory -and $_.name -ne "$packageName/manifest.json" }
    )
    if ($manifestFiles.Count -ne $expectedArchiveFiles.Count) {
        throw "manifest.json file count does not match archive content."
    }

    $previousPath = $null
    $seenManifestPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($manifestFile in $manifestFiles) {
        if ($manifestFile.path -isnot [string] -or
            [string]::IsNullOrWhiteSpace($manifestFile.path) -or
            $manifestFile.path -match '(^|/)\.\.(/|$)' -or
            [System.IO.Path]::IsPathFullyQualified($manifestFile.path) -or
            -not $seenManifestPaths.Add([string]$manifestFile.path)) {
            throw "manifest.json contains an unsafe path."
        }
        if ($null -ne $previousPath -and
            [string]::CompareOrdinal($previousPath, [string]$manifestFile.path) -ge 0) {
            throw "manifest.json files are not strictly ordinal sorted."
        }
        $previousPath = [string]$manifestFile.path
        $archiveEntry = $entryMap["$packageName/$($manifestFile.path)"]
        if ($null -eq $archiveEntry -or
            $archiveEntry.length -ne [int64]$manifestFile.length -or
            $archiveEntry.sha256 -ne $manifestFile.sha256 -or
            $archiveEntry.mode -ne $manifestFile.mode) {
            throw "manifest.json does not match archive entry '$($manifestFile.path)'."
        }
    }

    return [pscustomobject]@{
        Container = $container
        ArchivePath = $archivePath
        ArchiveSha256 = $actualArchiveHash
        Manifest = $manifest
    }
}

function Compare-Releases {
    param(
        [Parameter(Mandatory)][object] $Left,
        [Parameter(Mandatory)][object] $Right
    )

    foreach ($property in @("schemaVersion", "version", "commit", "sourceTreeClean", "rid", "selfContained", "sdkVersion", "packageRoot")) {
        if ($Left.Manifest[$property] -ne $Right.Manifest[$property]) {
            throw "Release manifests differ at '$property'."
        }
    }

    $leftFiles = @($Left.Manifest.files)
    $rightFiles = @($Right.Manifest.files)
    if ($leftFiles.Count -ne $rightFiles.Count) {
        throw "Release manifests have different file counts."
    }

    for ($index = 0; $index -lt $leftFiles.Count; $index++) {
        $leftFile = $leftFiles[$index]
        $rightFile = $rightFiles[$index]
        if ($leftFile.path -ne $rightFile.path -or
            $leftFile.length -ne $rightFile.length -or
            $leftFile.mode -ne $rightFile.mode) {
            throw "Release manifests differ structurally at file index $index."
        }
        if ($leftFile.sha256 -ne $rightFile.sha256) {
            throw "Release manifests differ at file '$($leftFile.path)'."
        }
    }

    if ($Left.ArchiveSha256 -eq $Right.ArchiveSha256) {
        Write-Host "Repeated archive comparison passed: byte-identical archive SHA-256."
        return
    }
    throw "Repeated archive hashes differ despite identical manifest files."
}

Assert-ReleaseVersion $Version
$resolvedOutputRoot = Resolve-PathInside $OutputRoot $artifactsRoot "OutputRoot" -AllowRoot
Assert-NoReparsePointAncestors $resolvedOutputRoot $artifactsRoot
$primary = Get-ReleaseSummary $resolvedOutputRoot $Version -PermitDirtySource:$AllowDirtySource

if (-not [string]::IsNullOrWhiteSpace($CompareOutputRoot)) {
    $resolvedCompareOutputRoot = Resolve-PathInside $CompareOutputRoot $artifactsRoot "CompareOutputRoot" -AllowRoot
    if ($resolvedCompareOutputRoot.Equals($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "CompareOutputRoot must identify a distinct release build root."
    }
    Assert-NoReparsePointAncestors $resolvedCompareOutputRoot $artifactsRoot
    $comparison = Get-ReleaseSummary $resolvedCompareOutputRoot $Version -PermitDirtySource:$AllowDirtySource
    Compare-Releases $primary $comparison
}

Write-Host "RelayCove Server release verification passed: $($primary.ArchivePath)"
