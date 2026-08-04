[CmdletBinding()]
param(
    [ValidatePattern('^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $OldVersion = "1.0.0-rc.11",

    [ValidatePattern('^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $NewVersion = "1.0.0-rc.12",

    [switch] $Publish,

    [string] $OldArchivePath,

    [string] $NewArchivePath,

    [int] $Port = 0,

    [switch] $KeepServerLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This is an internal-RC delivery smoke, not a replacement for the Client's
# coordinator/UI tests. It starts the real Server, streams a real HTTPS
# artifact to a client-equivalent .part file, and drives the published updater
# executable. The Updater never kills the Client: after evidence is captured,
# this harness terminates only the exact Client PID it observed from its unique
# smoke package path, so it does not leave an RC Client process behind. Windows
# SpecialFolder.LocalApplicationData cannot be isolated with a process variable,
# so production Client ownership-record cleanup remains unit-tested rather than
# touching a real user's local application data from this smoke.

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$publishScript = Join-Path $PSScriptRoot "publish-client.ps1"
$manifestScript = Join-Path $PSScriptRoot "generate-update-manifest.ps1"
$serverProject = Join-Path $repositoryRoot "src\RelayCove.Server\RelayCove.Server.csproj"
$serverDll = Join-Path $repositoryRoot "src\RelayCove.Server\bin\Release\net10.0\RelayCove.Server.dll"

function Assert-Condition {
    param([Parameter(Mandatory)][bool] $Condition, [Parameter(Mandatory)][string] $Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-PathInsideArtifacts {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Description)

    $root = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\', '/')
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be an absolute path inside $root."
    }

    return $resolved
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-Checked {
    param([Parameter(Mandatory)][string] $FilePath, [Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-ArchivePath {
    param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $ReleaseRoot)

    $packageName = "RelayCove.Client-$Version-win-x64"
    return Join-Path (Join-Path (Join-Path $ReleaseRoot "client") $Version) "$packageName.zip"
}

function Get-ArchivePackageName {
    param([Parameter(Mandatory)][string] $ArchivePath)

    $stream = [System.IO.File]::OpenRead($ArchivePath)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        $manifest = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/manifest.json', [System.StringComparison]::Ordinal) })
        Assert-Condition ($manifest.Count -eq 1) "Published archive must contain exactly one package manifest."
        return $manifest[0].FullName.Substring(0, $manifest[0].FullName.Length - '/manifest.json'.Length)
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $stream.Dispose()
    }
}

function Get-ArchiveCommit {
    param([Parameter(Mandatory)][string] $ArchivePath)

    $stream = [System.IO.File]::OpenRead($ArchivePath)
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        $manifest = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/manifest.json', [System.StringComparison]::Ordinal) })
        Assert-Condition ($manifest.Count -eq 1) "Published archive must contain exactly one package manifest."
        $reader = [System.IO.StreamReader]::new($manifest[0].Open(), [System.Text.UTF8Encoding]::new($false, $true))
        try {
            $commit = ($reader.ReadToEnd() | ConvertFrom-Json).commit
        }
        finally {
            $reader.Dispose()
        }
        Assert-Condition ($commit -cmatch '^[0-9a-f]{40}$') "Published archive has no valid embedded source commit."
        return $commit
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $stream.Dispose()
    }
}

function Expand-ArchiveExactly {
    param([Parameter(Mandatory)][string] $ArchivePath, [Parameter(Mandatory)][string] $Destination)

    Assert-Condition (-not (Test-Path -LiteralPath $Destination)) "Refusing to overwrite smoke target: $Destination"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, (Split-Path -Parent $Destination))
    $packageName = Get-ArchivePackageName $ArchivePath
    $extracted = Join-Path (Split-Path -Parent $Destination) $packageName
    Assert-Condition (Test-Path -LiteralPath $extracted -PathType Container) "Archive did not extract its expected package root."
    Move-Item -LiteralPath $extracted -Destination $Destination
}

function Read-PackageManifestVersion {
    param([Parameter(Mandatory)][string] $PackageRoot)

    $manifestPath = Join-Path $PackageRoot "manifest.json"
    Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Package manifest is missing: $manifestPath"
    return (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version
}

function New-TlsCertificate {
    param([Parameter(Mandatory)][string] $PfxPath, [Parameter(Mandatory)][string] $Password)

    $certificate = New-SelfSignedCertificate -DnsName "localhost" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddHours(2)
    try {
        $securePassword = ConvertTo-SecureString $Password -AsPlainText -Force
        Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password $securePassword | Out-Null
        return $certificate.Thumbprint
    }
    catch {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificate.Thumbprint) -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Start-SmokeServer {
    param(
        [Parameter(Mandatory)][int] $HttpsPort,
        [Parameter(Mandatory)][string] $ManifestPath,
        [Parameter(Mandatory)][string] $PfxPath,
        [Parameter(Mandatory)][string] $PfxPassword,
        [Parameter(Mandatory)][string] $RunRoot
    )

    Invoke-Checked dotnet build $serverProject --configuration Release --no-restore | Out-Host
    Assert-Condition (Test-Path -LiteralPath $serverDll -PathType Leaf) "Server build output is missing: $serverDll"
    $serverDataRoot = Join-Path $RunRoot "server-data"
    New-Item -ItemType Directory -Path $serverDataRoot | Out-Null
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $serverDataRoot
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add($serverDll)
    $startInfo.Environment["ASPNETCORE_URLS"] = "https://localhost:$HttpsPort"
    $startInfo.Environment["ConnectionStrings__Default"] = "Data Source=$(Join-Path $serverDataRoot 'relaycove-smoke.db');Foreign Keys=True;Default Timeout=5"
    $startInfo.Environment["Update__ManifestPath"] = $ManifestPath
    $startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = $PfxPath
    $startInfo.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = $PfxPassword
    # The isolated working directory intentionally does not load the repository
    # appsettings.json, so supply the non-secret ValidateOnStart defaults here.
    $startInfo.Environment["Authentication__Issuer"] = "RelayCove.Server"
    $startInfo.Environment["Authentication__Audience"] = "RelayCove.Client"
    $startInfo.Environment["Authentication__ServerVersion"] = "1.0.0"
    $startInfo.Environment["Authentication__MinimumSupportedClientVersion"] = "1.0.0"
    $signingKey = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($signingKey)
    $startInfo.Environment["Authentication__SigningKey"] = [Convert]::ToBase64String($signingKey)
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Assert-Condition ($null -ne $process) "Could not start the Server smoke process."
    return [pscustomobject]@{ Process = $process }
}

function New-SmokeHttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    # The Kestrel instance has an ephemeral test-only self-signed certificate.
    # Production Client TLS validation is deliberately not relaxed.
    $handler.ServerCertificateCustomValidationCallback = { $true }
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity") | Out-Null
    return $client
}

function Wait-ForServer {
    param([Parameter(Mandatory)][System.Net.Http.HttpClient] $Client, [Parameter(Mandatory)][uri] $BaseUri, [Parameter(Mandatory)][System.Diagnostics.Process] $Process)

    $uri = [uri]::new($BaseUri, "api/updates/manifest")
    $lastStatus = "no HTTP response"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($Process.HasExited) {
            throw "Server exited before becoming ready (exit code $($Process.ExitCode))."
        }
        try {
            $response = $Client.GetAsync($uri).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) { return }
                $lastStatus = "HTTP $([int]$response.StatusCode) $($response.ReasonPhrase)"
            }
            finally { $response.Dispose() }
        }
        catch [System.Net.Http.HttpRequestException] {
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for Kestrel HTTPS update endpoint; last result: $lastStatus."
}

function Get-HostedManifest {
    param([Parameter(Mandatory)][System.Net.Http.HttpClient] $Client, [Parameter(Mandatory)][uri] $BaseUri)

    $uri = [uri]::new($BaseUri, "api/updates/manifest")
    $response = $Client.GetAsync($uri).GetAwaiter().GetResult()
    try {
        Assert-Condition ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) "Manifest endpoint did not return HTTP 200."
        Assert-Condition ($response.Content.Headers.ContentType.MediaType -eq "application/json") "Manifest endpoint did not return JSON."
        Assert-Condition ($response.Headers.CacheControl.NoStore) "Manifest endpoint must be no-store."
        return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
    }
    finally { $response.Dispose() }
}

function Download-ClientEquivalentArchive {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient] $Client,
        [Parameter(Mandatory)] $Manifest,
        [Parameter(Mandatory)][string] $CacheRoot
    )

    New-Item -ItemType Directory -Path $CacheRoot | Out-Null
    $finalPath = Join-Path $CacheRoot ("RelayCove-" + $Manifest.version + ".zip")
    $partPath = "$finalPath.part"
    Assert-Condition (-not (Test-Path -LiteralPath $finalPath)) "Smoke download final already exists."
    Assert-Condition (-not (Test-Path -LiteralPath $partPath)) "Smoke download .part already exists."
    $expectedUri = [uri]$Manifest.artifact.url
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $expectedUri)
    $request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity") | Out-Null
    $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        Assert-Condition ($response.StatusCode -eq [System.Net.HttpStatusCode]::OK) "Artifact endpoint did not return HTTP 200."
        Assert-Condition ($response.RequestMessage.RequestUri.AbsoluteUri -eq $expectedUri.AbsoluteUri) "Artifact request unexpectedly redirected."
        Assert-Condition ($response.Content.Headers.ContentLength -eq [int64]$Manifest.artifact.sizeBytes) "Artifact content length differs from manifest."
        Assert-Condition ($response.Content.Headers.ContentRange -eq $null) "Artifact must not be a range response."
        Assert-Condition ($response.Content.Headers.ContentEncoding.Count -eq 0) "Artifact must not be content encoded."
        Assert-Condition ($response.Headers.ETag.Tag -eq ('"' + $Manifest.artifact.sha256 + '"')) "Artifact ETag differs from manifest SHA-256."
        $input = $response.Content.ReadAsStream()
        $output = [System.IO.FileStream]::new($partPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 81920, [System.IO.FileOptions]::SequentialScan)
        $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $buffer = [byte[]]::new(81920)
            [long] $written = 0
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                Assert-Condition ($read -le ([int64]$Manifest.artifact.sizeBytes - $written)) "Artifact exceeded its manifest length."
                $output.Write($buffer, 0, $read)
                $hash.AppendData($buffer, 0, $read)
                $written += $read
            }
            $output.Flush($true)
            $actualHash = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            Assert-Condition ($written -eq [int64]$Manifest.artifact.sizeBytes) "Artifact stream length differs from manifest."
            Assert-Condition ($actualHash -eq $Manifest.artifact.sha256) "Artifact stream SHA-256 differs from manifest."
        }
        finally {
            $hash.Dispose()
            $output.Dispose()
            $input.Dispose()
        }
        [System.IO.File]::Move($partPath, $finalPath)
        Assert-Condition (Test-Path -LiteralPath $finalPath -PathType Leaf) "Atomic archive publication failed."
        Assert-Condition (-not (Test-Path -LiteralPath $partPath)) "Atomic archive publication left a .part file."
        return $finalPath
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }
}

function Start-ProcessWithArguments {
    param([Parameter(Mandatory)][string] $FileName, [Parameter(Mandatory)][string] $WorkingDirectory, [Parameter(Mandatory)][string[]] $Arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FileName)
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Assert-Condition ($null -ne $process) "Could not start $FileName."
    return $process
}

function Start-ControlledExitProcess {
    # This represents the Client's controlled explicit Exit without sending a
    # destructive kill to a WPF process.  Its PID and start ticks are passed
    # exactly to the production updater, which verifies both before waiting.
    $process = Start-Process -FilePath "pwsh" -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 3") -PassThru -WindowStyle Hidden
    return [pscustomobject]@{ Process = $process; StartTicks = $process.StartTime.ToUniversalTime().Ticks }
}

function Invoke-PackageLocalUpdater {
    param(
        [Parameter(Mandatory)][string] $Target,
        [Parameter(Mandatory)][string] $Archive,
        [Parameter(Mandatory)][string] $ExpectedSha256,
        [Parameter(Mandatory)][long] $ExpectedSize,
        [Parameter(Mandatory)][string] $ExpectedVersion,
        [Parameter(Mandatory)][string] $CurrentVersion,
        [Parameter(Mandatory)] $WaitProcess,
        [Parameter(Mandatory)][string] $Token
    )

    $updaterPath = Join-Path $Target "RelayCove.Updater.exe"
    Assert-Condition (Test-Path -LiteralPath $updaterPath -PathType Leaf) "Package-local updater is missing."
    $arguments = @(
        "apply", "--archive", $Archive,
        "--expected-sha256", $ExpectedSha256,
        "--expected-size", $ExpectedSize.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "--expected-version", $ExpectedVersion,
        "--current-version", $CurrentVersion,
        "--target", $Target,
        "--wait-pid", $WaitProcess.Process.Id.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "--wait-start-time-utc-ticks", $WaitProcess.StartTicks.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "--bootstrap-token", $Token)
    $parent = Start-ProcessWithArguments $updaterPath $Target $arguments
    Assert-Condition ($parent.WaitForExit(15000)) "Package-local bootstrap parent did not exit."
    Assert-Condition ($parent.ExitCode -eq 0) "Package-local bootstrap parent rejected the handoff (exit code $($parent.ExitCode))."
    $parent.Dispose()
}

function Wait-ForManifestVersion {
    param([Parameter(Mandatory)][string] $Target, [Parameter(Mandatory)][string] $ExpectedVersion)

    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if ((Test-Path -LiteralPath $Target -PathType Container) -and
            (Read-PackageManifestVersion $Target) -eq $ExpectedVersion) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for updater to activate $ExpectedVersion."
}

function Wait-ForBootstrapDirectory {
    param([Parameter(Mandatory)][string] $Path)

    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (Test-Path -LiteralPath $Path -PathType Container) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "Updater did not create its external bootstrap directory."
}

function Get-ClientProcessIdsAtPath {
    param([Parameter(Mandatory)][string] $ClientExecutablePath)

    $ids = [System.Collections.Generic.List[int]]::new()
    foreach ($process in @(Get-Process -Name "RelayCove.Client" -ErrorAction SilentlyContinue)) {
        try {
            if ([string]::Equals($process.Path, $ClientExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $ids.Add($process.Id)
            }
        }
        catch {
            # A process can exit between enumeration and reading Path.
        }
        finally {
            $process.Dispose()
        }
    }
    return @($ids)
}

function Wait-ForNewClientStart {
    param([Parameter(Mandatory)][string] $ClientExecutablePath, [Parameter(Mandatory)][int[]] $ExistingIds)

    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $started = @(Get-ClientProcessIdsAtPath $ClientExecutablePath | Where-Object { $_ -notin $ExistingIds })
        if ($started.Count -gt 0) {
            return $started
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Updater activated the package but no new Client process was observed."
}

function Wait-ForProcessAtPath {
    param([Parameter(Mandatory)][string] $ExecutablePath)

    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
            try {
                if ([string]::Equals($process.Path, $ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $process.Id
                }
            }
            catch {
            }
            finally {
                $process.Dispose()
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for process: $ExecutablePath"
}

function Wait-ForProcessExitAtPath {
    param([Parameter(Mandatory)][string] $ExecutablePath)

    for ($attempt = 0; $attempt -lt 300; $attempt++) {
        $found = $false
        foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
            try {
                if ([string]::Equals($process.Path, $ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $found = $true
                }
            }
            catch {
            }
            finally {
                $process.Dispose()
            }
        }
        if (-not $found) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for process exit: $ExecutablePath"
}

function Remove-ExactOwnedBootstrapDirectory {
    param([Parameter(Mandatory)][string] $Directory, [Parameter(Mandatory)][string] $Token)

    Assert-Condition (Test-Path -LiteralPath $Directory -PathType Container) "Owned bootstrap directory is missing."
    Assert-Condition (((Get-Item -LiteralPath $Directory -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) "Owned bootstrap directory is a reparse point."
    $updater = Join-Path $Directory "RelayCove.Updater.exe"
    $marker = Join-Path $Directory ".relaycove-bootstrap-owner"
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    Assert-Condition ($entries.Count -eq 2) "Owned bootstrap directory has unexpected entries."
    foreach ($entry in $entries) {
        Assert-Condition (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) "Owned bootstrap directory contains a reparse point."
        Assert-Condition ($entry.FullName -eq $updater -or $entry.FullName -eq $marker) "Owned bootstrap directory contains an unexpected entry."
    }
    Assert-Condition (Test-Path -LiteralPath $updater -PathType Leaf) "Owned bootstrap updater is missing."
    Assert-Condition (Test-Path -LiteralPath $marker -PathType Leaf) "Owned bootstrap marker is missing."
    Assert-Condition ((Get-Content -LiteralPath $marker -Raw) -eq ("relaycove-bootstrap-owner:" + $Token)) "Owned bootstrap marker does not exactly match its token."
    [System.IO.File]::Delete($updater)
    [System.IO.File]::Delete($marker)
    [System.IO.Directory]::Delete($Directory, $false)
}

$gitChanges = @(git -C $repositoryRoot status --porcelain)
Assert-Condition ($gitChanges.Count -eq 0) "Refusing to publish or smoke a dirty checkout. Wait for all branch changes to be committed."
Assert-Condition ($OldVersion -ne $NewVersion) "OldVersion and NewVersion must differ."
Assert-Condition (([System.Management.Automation.PSTypeName]'System.IO.Compression.ZipFile').Type -ne $null) "System.IO.Compression.ZipFile is unavailable."
Assert-Condition (@(Get-Process -Name "RelayCove.Client" -ErrorAction SilentlyContinue).Count -eq 0) "Refusing to run while a RelayCove.Client process already exists."
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$runRoot = Assert-PathInsideArtifacts (Join-Path $artifactsRoot (Join-Path "update-delivery-smoke" ([Guid]::NewGuid().ToString("N")))) "Smoke run root"
New-Item -ItemType Directory -Path $runRoot | Out-Null

$server = $null
$certificateThumbprint = $null
$httpClient = $null
$smokeClientIds = @()
try {
    $releaseRoot = Join-Path $runRoot "releases"
    New-Item -ItemType Directory -Path $releaseRoot | Out-Null
    if ($Publish) {
        Invoke-Checked pwsh $publishScript -Version $OldVersion -OutputRoot $releaseRoot
        Invoke-Checked pwsh $publishScript -Version $NewVersion -OutputRoot $releaseRoot
        $OldArchivePath = Get-ArchivePath $OldVersion $releaseRoot
        $NewArchivePath = Get-ArchivePath $NewVersion $releaseRoot
    }
    else {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($OldArchivePath)) "Pass -Publish or an absolute -OldArchivePath."
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($NewArchivePath)) "Pass -Publish or an absolute -NewArchivePath."
        $OldArchivePath = [System.IO.Path]::GetFullPath($OldArchivePath)
        $NewArchivePath = [System.IO.Path]::GetFullPath($NewArchivePath)
    }
    Assert-Condition (Test-Path -LiteralPath $OldArchivePath -PathType Leaf) "Old archive is missing: $OldArchivePath"
    Assert-Condition (Test-Path -LiteralPath $NewArchivePath -PathType Leaf) "New archive is missing: $NewArchivePath"
    Invoke-Checked pwsh (Join-Path $PSScriptRoot "verify-client-release.ps1") -Version $OldVersion -OutputRoot (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $OldArchivePath))) -ExpectedCommit (Get-ArchiveCommit $OldArchivePath) -AllowDirtySource
    Invoke-Checked pwsh (Join-Path $PSScriptRoot "verify-client-release.ps1") -Version $NewVersion -OutputRoot (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $NewArchivePath))) -ExpectedCommit (Get-ArchiveCommit $NewArchivePath) -AllowDirtySource

    if ($Port -eq 0) { $Port = Get-FreeLoopbackPort }
    Assert-Condition ($Port -ge 1 -and $Port -le 65535) "Port must be between 1 and 65535."
    $baseUri = [uri]("https://localhost:{0}/" -f $Port)
    $hostRoot = Join-Path $runRoot "host"
    $artifactFileName = [System.IO.Path]::GetFileName($NewArchivePath)
    $manifestDirectory = Join-Path $hostRoot (Join-Path "updates\internal-rc" $NewVersion)
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    Copy-Item -LiteralPath $NewArchivePath -Destination (Join-Path $manifestDirectory $artifactFileName)
    Invoke-Checked pwsh $manifestScript -Version $NewVersion -MinimumSupportedVersion $NewVersion -Mandatory -DownloadUrl ($baseUri.AbsoluteUri + "api/updates/artifacts/" + $artifactFileName) -ReleaseNotes "Internal RC mandatory delivery smoke." -ClientReleaseRoot (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $NewArchivePath))) -OutputRoot $hostRoot -ExpectedCommit (Get-ArchiveCommit $NewArchivePath) -AllowDirtySource
    $manifestPath = Join-Path $manifestDirectory "manifest.json"
    Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Update manifest generator did not create the expected manifest."

    $tlsRoot = Join-Path $runRoot "tls"
    New-Item -ItemType Directory -Path $tlsRoot | Out-Null
    $pfxPath = Join-Path $tlsRoot "kestrel-smoke.pfx"
    $pfxPassword = [Guid]::NewGuid().ToString("N")
    $certificateThumbprint = New-TlsCertificate $pfxPath $pfxPassword
    $server = Start-SmokeServer $Port $manifestPath $pfxPath $pfxPassword $runRoot
    $httpClient = New-SmokeHttpClient
    Wait-ForServer $httpClient $baseUri $server.Process
    $manifest = Get-HostedManifest $httpClient $baseUri
    Assert-Condition ($manifest.version -eq $NewVersion) "Hosted manifest version differs from requested release."
    Assert-Condition ($manifest.artifact.url -eq ($baseUri.AbsoluteUri + "api/updates/artifacts/" + $artifactFileName)) "Hosted artifact URL is not the exact Kestrel route."
    $downloadRoot = Join-Path $runRoot "download-cache"
    $downloadedArchive = Download-ClientEquivalentArchive $httpClient $manifest $downloadRoot
    Assert-Condition ((Get-FileHash -LiteralPath $downloadedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.artifact.sha256) "Downloaded archive hash changed after atomic publication."

    $target = Join-Path (Join-Path $runRoot "portable") "RelayCove"
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) | Out-Null
    Expand-ArchiveExactly $OldArchivePath $target
    Assert-Condition ((Read-PackageManifestVersion $target) -eq $OldVersion) "Old package was not prepared as the updater target."
    $corruptArchive = Join-Path $runRoot "corrupt.zip"
    Copy-Item -LiteralPath $downloadedArchive -Destination $corruptArchive
    $corruptStream = [System.IO.File]::Open($corruptArchive, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $corruptStream.Position = 0
        $first = $corruptStream.ReadByte()
        $corruptStream.Position = 0
        $corruptStream.WriteByte([byte]($first -bxor 0x01))
        $corruptStream.Flush($true)
    }
    finally { $corruptStream.Dispose() }
    $corruptToken = [Guid]::NewGuid().ToString("N")
    $corruptBootstrapDirectory = Join-Path (Split-Path -Parent $target) (".relaycove-updater-" + $corruptToken)
    $failedWait = Start-ControlledExitProcess
    Invoke-PackageLocalUpdater $target $corruptArchive $manifest.artifact.sha256 ([int64]$manifest.artifact.sizeBytes) $NewVersion $OldVersion $failedWait $corruptToken
    Wait-ForBootstrapDirectory $corruptBootstrapDirectory
    $corruptBootstrapUpdater = Join-Path $corruptBootstrapDirectory "RelayCove.Updater.exe"
    Wait-ForProcessAtPath $corruptBootstrapUpdater | Out-Null
    $failedWait.Process.WaitForExit()
    Wait-ForProcessExitAtPath $corruptBootstrapUpdater
    Assert-Condition ((Read-PackageManifestVersion $target) -eq $OldVersion) "A corrupt package changed the old target."
    Assert-Condition (Test-Path -LiteralPath (Join-Path $target "RelayCove.Client.exe") -PathType Leaf) "Corrupt package handling removed the old Client executable."

    $token = [Guid]::NewGuid().ToString("N")
    $bootstrapDirectory = Join-Path (Split-Path -Parent $target) (".relaycove-updater-" + $token)
    $siblingToken = [Guid]::NewGuid().ToString("N")
    $unrelatedSibling = Join-Path (Split-Path -Parent $target) (".relaycove-updater-" + $siblingToken)
    New-Item -ItemType Directory -Path $unrelatedSibling | Out-Null
    Set-Content -LiteralPath (Join-Path $unrelatedSibling ".relaycove-bootstrap-owner") -Value ("relaycove-bootstrap-owner:" + $siblingToken) -NoNewline
    Set-Content -LiteralPath (Join-Path $unrelatedSibling "unrelated.txt") -Value "must remain" -NoNewline
    $clientExecutablePath = Join-Path $target "RelayCove.Client.exe"
    $existingClientIds = @(Get-ClientProcessIdsAtPath $clientExecutablePath)
    $successWait = Start-ControlledExitProcess
    Invoke-PackageLocalUpdater $target $downloadedArchive $manifest.artifact.sha256 ([int64]$manifest.artifact.sizeBytes) $NewVersion $OldVersion $successWait $token
    Wait-ForBootstrapDirectory $bootstrapDirectory
    $markerPath = Join-Path $bootstrapDirectory ".relaycove-bootstrap-owner"
    Assert-Condition ((Get-Content -LiteralPath $markerPath -Raw) -eq ("relaycove-bootstrap-owner:" + $token)) "Bootstrap owner marker does not exactly match its token."
    $successWait.Process.WaitForExit()
    Wait-ForManifestVersion $target $NewVersion
    Assert-Condition (Test-Path -LiteralPath (Join-Path $target "RelayCove.Client.exe") -PathType Leaf) "Updater did not activate a runnable Client executable."
    $newClientIds = @(Wait-ForNewClientStart $clientExecutablePath $existingClientIds)
    $smokeClientIds = $newClientIds
    Wait-ForProcessExitAtPath (Join-Path $bootstrapDirectory "RelayCove.Updater.exe")
    Remove-ExactOwnedBootstrapDirectory $bootstrapDirectory $token
    Assert-Condition (-not (Test-Path -LiteralPath $bootstrapDirectory)) "Exact owned bootstrap directory cleanup failed."
    Assert-Condition (Test-Path -LiteralPath $unrelatedSibling -PathType Container) "Updater deleted an unrelated bootstrap-looking sibling."
    Assert-Condition (Test-Path -LiteralPath (Join-Path $unrelatedSibling "unrelated.txt") -PathType Leaf) "Updater changed an unrelated sibling directory."

    $evidence = [ordered]@{
        oldVersion = $OldVersion
        newVersion = $NewVersion
        endpoint = $baseUri.AbsoluteUri
        manifestPath = $manifestPath
        downloadedArchive = $downloadedArchive
        target = $target
        observedNewClientProcessIds = $newClientIds
        bootstrapDirectory = $bootstrapDirectory
        unrelatedSibling = $unrelatedSibling
        notes = "Real Kestrel HTTPS, real Server hosting, client-equivalent streaming download, published package-local Updater, precise wait-process exit, exact bootstrap marker/path validation, strict smoke-owned bootstrap cleanup, unrelated sibling preservation, and observed new Client start. The Updater never kills the Client; after evidence, the harness terminates only its exact observed smoke Client PID. Production Client ownership-record cleanup is covered by Client tests because Windows LocalApplicationData cannot be safely isolated for a real WPF process."
    }
    $evidencePath = Join-Path $runRoot "evidence.json"
    $evidence | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Host "Update delivery smoke passed. Evidence: $evidencePath"
}
finally {
    if ($null -ne $httpClient) { $httpClient.Dispose() }
    foreach ($clientId in $smokeClientIds) {
        $client = Get-Process -Id $clientId -ErrorAction SilentlyContinue
        if ($null -ne $client) {
            # The PID was observed from this run's isolated package path. This is
            # post-evidence smoke cleanup, never the Updater's Client-exit strategy.
            $client.Kill($true)
            $client.WaitForExit()
            $client.Dispose()
        }
    }
    if ($null -ne $server) {
        if (-not $server.Process.HasExited) {
            $server.Process.Kill($true)
            $server.Process.WaitForExit()
        }
        $server.Process.Dispose()
    }
    if ($null -ne $certificateThumbprint) {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificateThumbprint) -Force -ErrorAction SilentlyContinue
    }
}
