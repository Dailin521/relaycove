function Resolve-RelayCoveServerAdminRoot {
    param([string]$RequestedRoot)

    $candidates = @(
        $RequestedRoot,
        [Environment]::GetEnvironmentVariable("RELAYCOVE_SERVER_ADMIN_ROOT"),
        "E:\GitHubProject\server-admin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (
            (Test-Path -LiteralPath (Join-Path $resolved "AI_INDEX.md") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $resolved "scripts/local/ssh_zulip_exec.py") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $resolved "scripts/local/ssh_upload.py") -PathType Leaf)
        ) {
            return $resolved
        }
    }

    throw "RelayCove Web deployment requires the private server-admin checkout. Pass -ServerAdminRoot or set RELAYCOVE_SERVER_ADMIN_ROOT."
}

function Assert-RelayCoveDeploymentToolchain {
    param([Parameter(Mandatory = $true)][string]$ServerAdminRoot)

    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "Python is required for the host-key-pinned server-admin SSH helpers."
    }
    foreach ($relativePath in @(
        "scripts/local/ssh_zulip_exec.py",
        "scripts/local/ssh_upload.py",
        "servers/zulip-hklight/known_hosts",
        "secrets/vps/hongkong_light_a2_zulip_host_info.txt"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $ServerAdminRoot $relativePath) -PathType Leaf)) {
            throw "Required server-admin deployment input is missing: $relativePath"
        }
    }
}

function Invoke-RelayCoveRemoteScript {
    param(
        [Parameter(Mandatory = $true)][string]$ServerAdminRoot,
        [Parameter(Mandatory = $true)][string]$Script
    )

    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script))
    & python (Join-Path $ServerAdminRoot "scripts/local/ssh_zulip_exec.py") $encoded --base64
    if ($LASTEXITCODE -ne 0) {
        throw "Remote RelayCove Web operation failed with exit code $LASTEXITCODE."
    }
}

function Send-RelayCoveRemoteFile {
    param(
        [Parameter(Mandatory = $true)][string]$ServerAdminRoot,
        [Parameter(Mandatory = $true)][string]$LocalPath,
        [Parameter(Mandatory = $true)][string]$RemotePath,
        [string]$Mode = "0644"
    )

    & python (Join-Path $ServerAdminRoot "scripts/local/ssh_upload.py") $LocalPath $RemotePath --mode $Mode
    if ($LASTEXITCODE -ne 0) {
        throw "RelayCove Web upload failed with exit code $LASTEXITCODE."
    }
}

function Expand-RelayCoveRemoteTemplate {
    param(
        [Parameter(Mandatory = $true)][string]$Template,
        [Parameter(Mandatory = $true)][hashtable]$Replacements
    )

    $expanded = $Template
    foreach ($token in $Replacements.Keys) {
        if ($token -notmatch '^__[A-Z0-9_]+__$') {
            throw "Unsafe remote-template token: $token"
        }
        if (-not $expanded.Contains($token)) {
            throw "Remote-template token is missing: $token"
        }
        $expanded = $expanded.Replace($token, [string]$Replacements[$token])
    }
    if ($expanded -match '__[A-Z0-9_]+__') {
        throw "Remote template contains an unresolved token: $($Matches[0])"
    }

    return $expanded
}
