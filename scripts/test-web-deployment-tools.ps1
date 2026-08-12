$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "web-deploy-common.ps1")

$template = "release='__RELEASE__'; archive='__ARCHIVE__'; hash='__HASH__'"
$rendered = Expand-RelayCoveRemoteTemplate -Template $template -Replacements @{
    '__RELEASE__' = '20260812T000000Z-abcdef123456-worktree'
    '__ARCHIVE__' = '/opt/relaycove-web/incoming/release.tar.gz'
    '__HASH__' = ('a' * 64)
}

if ($rendered -match '__[A-Z0-9_]+__') {
    throw "Deployment template regression: a token remained unresolved."
}
if ($rendered -notmatch "release='20260812T000000Z-abcdef123456-worktree'") {
    throw "Deployment template regression: the release identifier was not rendered."
}

$rejectedMissingToken = $false
try {
    Expand-RelayCoveRemoteTemplate -Template "value='__VALUE__'" -Replacements @{} | Out-Null
}
catch {
    $rejectedMissingToken = $true
}
if (-not $rejectedMissingToken) {
    throw "Deployment template regression: an unresolved token was accepted."
}

Write-Host "RelayCove Web deployment template tests passed."
