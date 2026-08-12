[CmdletBinding()]
param(
    [string]$ServerAdminRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$webRoot = Join-Path $repoRoot "src/RelayCove.Web"
$artifactRoot = Join-Path $repoRoot "artifacts/web/deploy"
. (Join-Path $PSScriptRoot "web-deploy-common.ps1")

$resolvedServerAdminRoot = Resolve-RelayCoveServerAdminRoot -RequestedRoot $ServerAdminRoot
Assert-RelayCoveDeploymentToolchain -ServerAdminRoot $resolvedServerAdminRoot
if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
    throw "tar.exe is required to create the versioned Web release archive."
}

Push-Location $webRoot
try {
    & npm run verify:full
    if ($LASTEXITCODE -ne 0) {
        throw "RelayCove Web verification failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$commit = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{12}$') {
    throw "Unable to resolve the RelayCove Git revision."
}
$workingTreeChanges = @(& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the RelayCove Git worktree."
}
$dirtySuffix = if ($workingTreeChanges.Count -gt 0) { "-worktree" } else { "" }
$releaseId = "{0}-{1}{2}" -f (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ"), $commit, $dirtySuffix
if ($releaseId -notmatch '^[0-9A-Za-z._-]+$') {
    throw "Generated release identifier is unsafe: $releaseId"
}

$releaseArtifacts = Join-Path $artifactRoot $releaseId
$packageRoot = Join-Path $releaseArtifacts "package"
$packageWebRoot = Join-Path $packageRoot "relaycove-web"
if (Test-Path -LiteralPath $releaseArtifacts) {
    throw "Deployment artifacts already exist: $releaseArtifacts"
}
New-Item -ItemType Directory -Path $packageWebRoot -Force | Out-Null
Copy-Item -Path (Join-Path $webRoot "dist/*") -Destination $packageWebRoot -Recurse

$archiveName = "relaycove-web-$releaseId.tar.gz"
$archivePath = Join-Path $releaseArtifacts $archiveName
& tar.exe -czf $archivePath -C $packageRoot "relaycove-web"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Failed to create the RelayCove Web release archive."
}
$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
$remoteArchive = "/opt/relaycove-web/incoming/$archiveName"

$preflightTemplate = @'
set -eu
release='__RELEASE__'
case "$release" in ''|*[!0-9A-Za-z._-]*) echo 'Unsafe release identifier.' >&2; exit 1 ;; esac
test "$(id -u)" -eq 0
test -f /etc/nginx/snippets/relaycove-web-locations.conf
test -f /etc/nginx/snippets/relaycove-web-security-headers.conf
grep -Fq 'include /etc/nginx/snippets/relaycove-web-locations.conf;' /etc/nginx/sites-available/hklight.2000521.xyz.conf
test ! -e "/opt/relaycove-web/releases/$release"
mkdir -p /opt/relaycove-web/releases /opt/relaycove-web/incoming
nginx -t
'@
$preflight = Expand-RelayCoveRemoteTemplate -Template $preflightTemplate -Replacements @{
    '__RELEASE__' = $releaseId
}
Invoke-RelayCoveRemoteScript -ServerAdminRoot $resolvedServerAdminRoot -Script $preflight
Send-RelayCoveRemoteFile -ServerAdminRoot $resolvedServerAdminRoot -LocalPath $archivePath -RemotePath $remoteArchive

$deployTemplate = @'
set -eu
root=/opt/relaycove-web
release='__RELEASE__'
archive='__ARCHIVE__'
expected='__HASH__'
staging="$root/releases/.$release.staging"
final="$root/releases/$release"
next_link="$root/.current-$release"
html_headers="$(mktemp)"
cleanup() { rm -f "$html_headers"; }
trap cleanup EXIT

test "$(id -u)" -eq 0
case "$release" in ''|*[!0-9A-Za-z._-]*) echo 'Unsafe release identifier.' >&2; exit 1 ;; esac
case "$archive" in /opt/relaycove-web/incoming/relaycove-web-*.tar.gz) ;; *) echo 'Unsafe incoming archive path.' >&2; exit 1 ;; esac
test "$(sha256sum "$archive" | awk '{print $1}')" = "$expected"
test ! -e "$staging"
test ! -e "$final"
mkdir -m 0755 "$staging"

if tar -tzf "$archive" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then
    echo 'Unsafe archive path detected.' >&2
    exit 1
fi
tar --warning=no-timestamp -xzf "$archive" -C "$staging"
test -f "$staging/relaycove-web/index.html"
test -n "$(find "$staging/relaycove-web/assets" -maxdepth 1 -type f -name 'index-*.js' -print -quit)"
test -n "$(find "$staging/relaycove-web/assets" -maxdepth 1 -type f -name 'index-*.css' -print -quit)"
if find "$staging" -type l -print -quit | grep -q .; then
    echo 'Release archive contains a symbolic link.' >&2
    exit 1
fi
find "$staging" -type d -exec chmod 0755 {} +
find "$staging" -type f -exec chmod 0644 {} +
chown -R root:root "$staging"
mv "$staging" "$final"

old_target="$(readlink "$root/current" 2>/dev/null || true)"
ln -s "releases/$release" "$next_link"
mv -Tf "$next_link" "$root/current"

asset_path="$(sed -n 's/.*src="\([^"]*\.js\)".*/\1/p' "$final/relaycove-web/index.html" | head -n 1)"
case "$asset_path" in /relaycove-web/assets/index-*.js) ;; *) asset_path='' ;; esac
missing_asset_status="$(curl -sS --resolve hklight.2000521.xyz:443:127.0.0.1 -o /dev/null -w '%{http_code}' https://hklight.2000521.xyz/relaycove-web/assets/not-a-real-build-asset.js)"
if ! nginx -t ||
    [ -z "$asset_path" ] ||
    ! curl -fsS --resolve hklight.2000521.xyz:443:127.0.0.1 -D "$html_headers" https://hklight.2000521.xyz/relaycove-web/ -o /dev/null ||
    ! grep -Fqi 'Cache-Control: no-cache, must-revalidate' "$html_headers" ||
    ! grep -Fqi "frame-ancestors 'none'" "$html_headers" ||
    ! grep -Fqi 'X-Content-Type-Options: nosniff' "$html_headers" ||
    ! curl -fsS --resolve hklight.2000521.xyz:443:127.0.0.1 "https://hklight.2000521.xyz$asset_path" -o /dev/null ||
    ! curl -fsS --resolve hklight.2000521.xyz:443:127.0.0.1 https://hklight.2000521.xyz/relaycove-web/relaycove.svg -o /dev/null ||
    [ "$missing_asset_status" != 404 ]; then
    if [ -n "$old_target" ]; then
        rollback_link="$root/.rollback-$release"
        ln -s "$old_target" "$rollback_link"
        mv -Tf "$rollback_link" "$root/current"
    else
        rm -f "$root/current"
    fi
    echo 'Deployment smoke check failed; current link restored.' >&2
    exit 1
fi

rm -f "$archive" || echo 'Warning: deployed successfully but could not remove the incoming archive.' >&2
echo "release=$release"
echo "previous=$old_target"
echo "sha256=$expected"
'@
$deploy = Expand-RelayCoveRemoteTemplate -Template $deployTemplate -Replacements @{
    '__RELEASE__' = $releaseId
    '__ARCHIVE__' = $remoteArchive
    '__HASH__' = $archiveHash
}

Invoke-RelayCoveRemoteScript -ServerAdminRoot $resolvedServerAdminRoot -Script $deploy
Write-Host "RelayCove Web deployed: https://hklight.2000521.xyz/relaycove-web/"
Write-Host "Release: $releaseId"
Write-Host "Archive SHA-256: $archiveHash"
