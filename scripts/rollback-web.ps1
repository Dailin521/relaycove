[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z._-]+$')]
    [string]$ReleaseId,
    [string]$ServerAdminRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "web-deploy-common.ps1")
$resolvedServerAdminRoot = Resolve-RelayCoveServerAdminRoot -RequestedRoot $ServerAdminRoot
Assert-RelayCoveDeploymentToolchain -ServerAdminRoot $resolvedServerAdminRoot

$remoteTemplate = @'
set -eu
root=/opt/relaycove-web
release='__RELEASE__'
target="$root/releases/$release"
case "$release" in ''|*[!0-9A-Za-z._-]*) echo 'Unsafe release identifier.' >&2; exit 1 ;; esac
test -f "$target/relaycove-web/index.html"
old_target="$(readlink "$root/current" 2>/dev/null || true)"
test -n "$old_target"
next_link="$root/.rollback-$release"
ln -s "releases/$release" "$next_link"
mv -Tf "$next_link" "$root/current"
if ! curl -fsS --resolve hklight.2000521.xyz:443:127.0.0.1 https://hklight.2000521.xyz/relaycove-web/ -o /dev/null; then
    restore_link="$root/.restore-$release"
    ln -s "$old_target" "$restore_link"
    mv -Tf "$restore_link" "$root/current"
    echo 'Rollback target failed smoke check; previous current restored.' >&2
    exit 1
fi
echo "current=$release"
echo "previous=$old_target"
'@
$remoteScript = Expand-RelayCoveRemoteTemplate -Template $remoteTemplate -Replacements @{
    '__RELEASE__' = $ReleaseId
}
Invoke-RelayCoveRemoteScript -ServerAdminRoot $resolvedServerAdminRoot -Script $remoteScript
Write-Host "RelayCove Web rolled back to $ReleaseId"
