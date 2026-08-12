[CmdletBinding()]
param(
    [string]$ServerAdminRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "web-deploy-common.ps1")

$resolvedServerAdminRoot = Resolve-RelayCoveServerAdminRoot -RequestedRoot $ServerAdminRoot
Assert-RelayCoveDeploymentToolchain -ServerAdminRoot $resolvedServerAdminRoot

$locationsPath = Join-Path $repoRoot "deploy/nginx/relaycove-web-locations.conf"
$headersPath = Join-Path $repoRoot "deploy/nginx/relaycove-web-security-headers.conf"
foreach ($path in @($locationsPath, $headersPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Deployment configuration is missing: $path"
    }
}

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$remoteLocations = "/tmp/relaycove-web-locations-$stamp.conf"
$remoteHeaders = "/tmp/relaycove-web-security-headers-$stamp.conf"
Send-RelayCoveRemoteFile -ServerAdminRoot $resolvedServerAdminRoot -LocalPath $locationsPath -RemotePath $remoteLocations
Send-RelayCoveRemoteFile -ServerAdminRoot $resolvedServerAdminRoot -LocalPath $headersPath -RemotePath $remoteHeaders

$remoteScript = @"
set -eu
site=/etc/nginx/sites-available/hklight.2000521.xyz.conf
enabled=/etc/nginx/sites-enabled/hklight.2000521.xyz.conf
locations=/etc/nginx/snippets/relaycove-web-locations.conf
headers=/etc/nginx/snippets/relaycove-web-security-headers.conf
uploaded_locations='$remoteLocations'
uploaded_headers='$remoteHeaders'
backup=/var/backups/relaycove-web/nginx/$stamp

test "`$(id -u)" -eq 0
test -f "`$site"
test "`$(readlink -f "`$enabled")" = "`$site"
test -f "`$uploaded_locations"
test -f "`$uploaded_headers"
mkdir -p "`$backup" /etc/nginx/snippets /opt/relaycove-web/releases /opt/relaycove-web/incoming
cp -a "`$site" "`$backup/site.conf"
if [ -f "`$locations" ]; then cp -a "`$locations" "`$backup/locations.conf"; fi
if [ -f "`$headers" ]; then cp -a "`$headers" "`$backup/security-headers.conf"; fi
install -o root -g root -m 0644 "`$uploaded_locations" "`$locations"
install -o root -g root -m 0644 "`$uploaded_headers" "`$headers"

python3 - "`$site" <<'PY'
import os
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
content = path.read_text(encoding="utf-8")
include = "    include /etc/nginx/snippets/relaycove-web-locations.conf;\n"
target = "    location / {\n        proxy_pass http://127.0.0.1:8080;"
if include not in content:
    if content.count(target) != 1:
        raise SystemExit("Refusing to edit an unexpected hklight Nginx layout")
    content = content.replace(target, f"{include}\n{target}", 1)
    temporary = path.with_name(f".{path.name}.relaycove-web.tmp")
    temporary.write_text(content, encoding="utf-8")
    os.chmod(temporary, 0o644)
    os.replace(temporary, path)
PY

if ! nginx -t; then
    cp -a "`$backup/site.conf" "`$site"
    if [ -f "`$backup/locations.conf" ]; then cp -a "`$backup/locations.conf" "`$locations"; else rm -f "`$locations"; fi
    if [ -f "`$backup/security-headers.conf" ]; then cp -a "`$backup/security-headers.conf" "`$headers"; else rm -f "`$headers"; fi
    nginx -t
    exit 1
fi

if ! systemctl reload nginx; then
    cp -a "`$backup/site.conf" "`$site"
    if [ -f "`$backup/locations.conf" ]; then cp -a "`$backup/locations.conf" "`$locations"; else rm -f "`$locations"; fi
    if [ -f "`$backup/security-headers.conf" ]; then cp -a "`$backup/security-headers.conf" "`$headers"; else rm -f "`$headers"; fi
    nginx -t
    systemctl reload nginx
    echo 'Nginx reload failed; the previous configuration was restored.' >&2
    exit 1
fi
rm -f "`$uploaded_locations" "`$uploaded_headers"
echo "provisioned=1"
echo "backup=`$backup"
"@

Invoke-RelayCoveRemoteScript -ServerAdminRoot $resolvedServerAdminRoot -Script $remoteScript
Write-Host "RelayCove Web host provisioned for https://hklight.2000521.xyz/relaycove-web/"
