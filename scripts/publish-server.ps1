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
$serverProject = Join-Path $repositoryRoot "src\RelayCove.Server\RelayCove.Server.csproj"
$deploymentSources = [ordered]@{
    "relaycove.service" = Join-Path $repositoryRoot "installer\linux\relaycove.service"
    "nginx.conf" = Join-Path $repositoryRoot "installer\linux\nginx.conf"
    "appsettings.Production.example.json" = Join-Path $repositoryRoot "installer\linux\appsettings.Production.example.json"
    "relaycove.env.example" = Join-Path $repositoryRoot "installer\linux\relaycove.env.example"
    "DEPLOYMENT.md" = Join-Path $repositoryRoot "docs\deployment.md"
}
$runtimeIdentifier = "linux-x64"
$archivePrefix = "RelayCove.Server"
$migrationBundleName = "RelayCove.Migrations"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

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

function Get-FileMode {
    param([Parameter(Mandatory)][string] $RelativePath)

    return $RelativePath -in @(
        "app/RelayCove.Server",
        "migrate/$migrationBundleName"
    ) ? 493 : 420
}

function Format-LinuxMode {
    param([Parameter(Mandatory)][int] $Mode)

    return [System.Convert]::ToString($Mode, 8).PadLeft(4, '0')
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

function Assert-PackagePaths {
    param(
        [Parameter(Mandatory)][string] $PackageRoot,
        [Parameter(Mandatory)][string] $PackageName,
        [Parameter(Mandatory)][string] $PackageVersion
    )

    $requiredFiles = @(
        "app/RelayCove.Server",
        "app/RelayCove.Server.deps.json",
        "app/RelayCove.Server.runtimeconfig.json",
        "migrate/$migrationBundleName",
        "deploy/relaycove.service",
        "deploy/nginx.conf",
        "deploy/appsettings.Production.example.json",
        "deploy/relaycove.env.example",
        "deploy/DEPLOYMENT.md"
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relativePath) -PathType Leaf)) {
            throw "Release package is missing required file '$relativePath'."
        }
    }

    $seenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -Recurse -File) {
        $relativePath = Get-RelativePath $PackageRoot $file.FullName
        if ([System.IO.Path]::IsPathFullyQualified($relativePath) -or
            $relativePath.Split('/') -contains ".." -or
            -not $seenPaths.Add($relativePath)) {
            throw "Release package contains an unsafe or duplicate path '$relativePath'."
        }

        if ($relativePath -match '(?i)(^|/)(bin|obj|uploads|logs)(/|$)|\.pdb$|\.cs$|\.csproj$|\.sln$|^app/appsettings\.(?!json$)|(^|/).*\.(db|sqlite)(-wal|-shm)?$') {
            throw "Release package contains forbidden file '$relativePath'."
        }
    }

    $applicationSettings = Get-Content -LiteralPath (Join-Path $PackageRoot "app\appsettings.json") -Raw |
        ConvertFrom-Json -AsHashtable
    if ($applicationSettings.Authentication.ContainsKey("SigningKey") -or
        ($applicationSettings.ContainsKey("BootstrapAdmin") -and
            ($applicationSettings.BootstrapAdmin.ContainsKey("Password") -or
             $applicationSettings.BootstrapAdmin.ContainsKey("UserName") -or
             $applicationSettings.BootstrapAdmin.ContainsKey("DisplayName")))) {
        throw "Application settings must not package authentication or bootstrap credentials."
    }

    Assert-EnvironmentExample ([System.IO.File]::ReadAllText(
            (Join-Path $PackageRoot "deploy\relaycove.env.example"),
            [System.Text.UTF8Encoding]::new($false)))
    $productionConfiguration = [System.IO.File]::ReadAllText(
        (Join-Path $PackageRoot "deploy\appsettings.Production.example.json"),
        [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json -AsHashtable
    Assert-ProductionConfiguration $productionConfiguration $PackageVersion
}

function Get-SortedPackageFiles {
    param([Parameter(Mandatory)][string] $PackageRoot)

    $filesByPath = @{}
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -Recurse -File) {
        $relativePath = Get-RelativePath $PackageRoot $file.FullName
        if ($filesByPath.ContainsKey($relativePath)) {
            throw "Release package contains a duplicate file path '$relativePath'."
        }
        $filesByPath.Add($relativePath, $file)
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

function Get-PackageFiles {
    param([Parameter(Mandatory)][string] $PackageRoot)

    return @(
        Get-SortedPackageFiles $PackageRoot |
            ForEach-Object {
                $relativePath = $_.RelativePath
                [ordered]@{
                    path = $relativePath
                    length = $_.File.Length
                    sha256 = (Get-FileHash -LiteralPath $_.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    mode = (Format-LinuxMode (Get-FileMode $relativePath))
                }
            }
    )
}

function New-DeterministicTarGzip {
    param(
        [Parameter(Mandatory)][string] $PackageRoot,
        [Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $PackageName
    )

    $archiveStream = $null
    $gzipStream = $null
    $tarWriter = $null
    try {
        $archiveStream = [System.IO.File]::Create($ArchivePath)
        $gzipStream = [System.IO.Compression.GZipStream]::new(
            $archiveStream,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
        $tarWriter = [System.Formats.Tar.TarWriter]::new(
            $gzipStream,
            [System.Formats.Tar.TarEntryFormat]::Ustar,
            $false)

        $rootEntry = [System.Formats.Tar.UstarTarEntry]::new(
            [System.Formats.Tar.TarEntryType]::Directory,
            "$PackageName/")
        $rootEntry.Mode = 493
        $rootEntry.ModificationTime = [System.DateTimeOffset]::UnixEpoch
        $rootEntry.Uid = 0
        $rootEntry.Gid = 0
        $rootEntry.UserName = ""
        $rootEntry.GroupName = ""
        $tarWriter.WriteEntry($rootEntry)

        $directories = @(
            Get-ChildItem -LiteralPath $PackageRoot -Recurse -Directory |
                ForEach-Object { Get-RelativePath $PackageRoot $_.FullName } |
                Sort-Object
        )
        foreach ($relativeDirectory in $directories) {
            $entry = [System.Formats.Tar.UstarTarEntry]::new(
                [System.Formats.Tar.TarEntryType]::Directory,
                "$PackageName/$relativeDirectory/")
            $entry.Mode = 493
            $entry.ModificationTime = [System.DateTimeOffset]::UnixEpoch
            $entry.Uid = 0
            $entry.Gid = 0
            $entry.UserName = ""
            $entry.GroupName = ""
            $tarWriter.WriteEntry($entry)
        }

        $files = Get-SortedPackageFiles $PackageRoot
        foreach ($fileInfo in $files) {
            $relativePath = $fileInfo.RelativePath
            $entry = [System.Formats.Tar.UstarTarEntry]::new(
                [System.Formats.Tar.TarEntryType]::RegularFile,
                "$PackageName/$relativePath")
            $entry.Mode = Get-FileMode $relativePath
            $entry.ModificationTime = [System.DateTimeOffset]::UnixEpoch
            $entry.Uid = 0
            $entry.Gid = 0
            $entry.UserName = ""
            $entry.GroupName = ""
            $dataStream = [System.IO.File]::OpenRead($fileInfo.File.FullName)
            try {
                $entry.DataStream = $dataStream
                $tarWriter.WriteEntry($entry)
            }
            finally {
                $dataStream.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $tarWriter) {
            $tarWriter.Dispose()
        }
        elseif ($null -ne $gzipStream) {
            $gzipStream.Dispose()
        }
        elseif ($null -ne $archiveStream) {
            $archiveStream.Dispose()
        }
    }
}

function New-DeterministicMigrationBundleProject {
    param(
        [Parameter(Mandatory)][string] $BundleProjectRoot,
        [Parameter(Mandatory)][string] $ServerProjectPath
    )

    New-Item -ItemType Directory -Path $BundleProjectRoot -Force | Out-Null
    $escapedServerProjectPath = [System.Security.SecurityElement]::Escape($ServerProjectPath)
    $escapedBundleProjectRoot = [System.Security.SecurityElement]::Escape($BundleProjectRoot)
    # This mirrors the EF Core 10.0.10 migrations-bundle generator, but fixes the
    # generated project path. The stock command uses a random temporary directory,
    # which leaks into generated assembly metadata and defeats RC reproducibility.
    $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$migrationBundleName</AssemblyName>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <ValidateExecutableReferencesMatchSelfContained>false</ValidateExecutableReferencesMatchSelfContained>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
    <DebugType>None</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <PathMap>$escapedBundleProjectRoot=/_/efbundle</PathMap>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
    <ProjectReference Include="$escapedServerProjectPath" GlobalPropertiesToRemove="SelfContained" />
  </ItemGroup>
</Project>
"@
    $programContent = @"
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations.Design;

return MigrationsBundle.Execute(
    "RelayCove.Server.Data.RelayCoveDbContext",
    Assembly.Load("RelayCove.Server"),
    Assembly.Load("RelayCove.Server"),
    args);
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $BundleProjectRoot "$migrationBundleName.csproj"),
        $projectContent,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $BundleProjectRoot "Program.cs"),
        $programContent,
        [System.Text.UTF8Encoding]::new($false))
}

Assert-ReleaseVersion $Version
if (-not (Test-Path -LiteralPath $serverProject -PathType Leaf)) {
    throw "Server project was not found: $serverProject"
}

$resolvedOutputRoot = Resolve-PathInside $OutputRoot $artifactsRoot "OutputRoot" -AllowRoot
Assert-NoReparsePointAncestors $resolvedOutputRoot $artifactsRoot
$sourceTreeClean = Test-GitClean
if (-not $sourceTreeClean -and -not $AllowDirty) {
    throw "Refusing to publish from a dirty Git checkout. Use -AllowDirty only for non-release local validation."
}

foreach ($deploymentSource in $deploymentSources.Values) {
    if (-not (Test-Path -LiteralPath $deploymentSource -PathType Leaf)) {
        throw "Required deployment material was not found: $deploymentSource"
    }
}

$commit = Get-GitOutput @("rev-parse", "--verify", "HEAD")
$sdkVersion = $null
Push-Location $repositoryRoot
try {
    $sdkVersion = (& dotnet --version | Out-String).Trim()
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "Unable to determine the .NET SDK version."
}

$packageName = "$archivePrefix-$Version-$runtimeIdentifier"
$finalContainer = Join-Path (Join-Path $resolvedOutputRoot "server") $Version
$stagingRoot = Resolve-PathInside (
    Join-Path $artifactsRoot (Join-Path ".staging" ([System.Guid]::NewGuid().ToString("N")))) $artifactsRoot "staging directory"
$stagedContainer = Join-Path (Join-Path $stagingRoot "server") $Version
$dotnetArtifactsPath = Join-Path $stagingRoot ".dotnet-artifacts"
$bundleWorkRoot = Resolve-PathInside (
    Join-Path $artifactsRoot (Join-Path ".migrations-bundle-work" $Version)) $artifactsRoot "migration bundle work directory"
$packageRoot = Join-Path $stagedContainer $packageName
$archiveFileName = "$packageName.tar.gz"
$archivePath = Join-Path $stagedContainer $archiveFileName

if (Test-Path -LiteralPath $finalContainer) {
    throw "Release output already exists and will not be overwritten: $finalContainer"
}
if (Test-Path -LiteralPath $bundleWorkRoot) {
    throw "Migration bundle work directory already exists and will not be overwritten: $bundleWorkRoot"
}

try {
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    Assert-NoReparsePointAncestors $stagingRoot $artifactsRoot
    Assert-NoReparsePointAncestors $bundleWorkRoot $artifactsRoot
    Assert-NoReparsePointAncestors $finalContainer $artifactsRoot
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "app") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "migrate") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "deploy") -Force | Out-Null

    Push-Location $repositoryRoot
    try {
        $commonProperties = @(
            "/p:Version=$Version",
            "/p:DebugType=None",
            "/p:DebugSymbols=false",
            "/p:ContinuousIntegrationBuild=true"
        )
        Invoke-Checked "dotnet" @(
            "restore", $serverProject,
            "--runtime", $runtimeIdentifier,
            "--artifacts-path", $dotnetArtifactsPath,
            "/p:Version=$Version"
        )
        $buildArguments = @(
            "build", $serverProject,
            "--configuration", "Release",
            "--runtime", $runtimeIdentifier,
            "--artifacts-path", $dotnetArtifactsPath,
            "--no-restore"
        ) + $commonProperties
        Invoke-Checked "dotnet" $buildArguments
        $publishArguments = @(
            "publish", $serverProject,
            "--configuration", "Release",
            "--runtime", $runtimeIdentifier,
            "--self-contained", "true",
            "--artifacts-path", $dotnetArtifactsPath,
            "--no-build", "--no-restore",
            "--output", (Join-Path $packageRoot "app")
        ) + $commonProperties
        Invoke-Checked "dotnet" $publishArguments
        New-DeterministicMigrationBundleProject $bundleWorkRoot $serverProject
        $bundleProject = Join-Path $bundleWorkRoot "$migrationBundleName.csproj"
        Invoke-Checked "dotnet" @(
            "restore", $bundleProject,
            "--runtime", $runtimeIdentifier,
            "--artifacts-path", $dotnetArtifactsPath,
            "/p:Version=$Version"
        )
        $bundleArguments = @(
            "publish", $bundleProject,
            "--configuration", "Release",
            "--runtime", $runtimeIdentifier,
            "--self-contained", "true",
            "--artifacts-path", $dotnetArtifactsPath,
            "--no-restore",
            "--output", (Join-Path $packageRoot "migrate")
        ) + $commonProperties
        Invoke-Checked "dotnet" $bundleArguments
        Get-ChildItem -LiteralPath (Join-Path $packageRoot "migrate") -Recurse -File |
            Where-Object { $_.Name -ne $migrationBundleName } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    finally {
        Pop-Location
    }

    Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter "appsettings.Development.json" |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    foreach ($item in $deploymentSources.GetEnumerator()) {
        $destination = Join-Path $packageRoot "deploy\$($item.Key)"
        if ($item.Key -ne "appsettings.Production.example.json") {
            [System.IO.File]::Copy($item.Value, $destination, $false)
            continue
        }

        $template = [System.IO.File]::ReadAllText($item.Value, [System.Text.UTF8Encoding]::new($false))
        $placeholder = "REPLACE_WITH_PACKAGE_VERSION"
        if ($template.Split($placeholder).Count -ne 2) {
            throw "Production configuration template must contain exactly one package-version placeholder."
        }
        [System.IO.File]::WriteAllText(
            $destination,
            $template.Replace($placeholder, $Version, [System.StringComparison]::Ordinal),
            [System.Text.UTF8Encoding]::new($false))
    }

    Assert-PackagePaths $packageRoot $packageName $Version
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        commit = $commit
        sourceTreeClean = $sourceTreeClean
        rid = $runtimeIdentifier
        selfContained = $true
        sdkVersion = $sdkVersion
        packageRoot = $packageName
        files = @(Get-PackageFiles $packageRoot)
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText(
        (Join-Path $packageRoot "manifest.json"),
        $manifestJson + [System.Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    New-DeterministicTarGzip $packageRoot $archivePath $packageName
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$archivePath.sha256",
        "$archiveHash  $archiveFileName$([System.Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path (Split-Path -Parent $finalContainer) -Force | Out-Null
    Move-Item -LiteralPath $stagedContainer -Destination (Split-Path -Parent $finalContainer)
    $stagedContainer = $null
    Write-Host "RelayCove Server release package created: $(Join-Path $finalContainer $archiveFileName)"
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
    if (Test-Path -LiteralPath $bundleWorkRoot) {
        Assert-NoReparsePointAncestors $bundleWorkRoot $artifactsRoot
        Remove-Item -LiteralPath $bundleWorkRoot -Recurse -Force
    }
}
