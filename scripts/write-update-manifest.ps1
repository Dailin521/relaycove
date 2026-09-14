[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [int]$BuildNumber
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $BuildNumber -le 0) {
    throw "A three-component version and positive build number are required."
}
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.PSIsContainer -or $installer.Length -le 0 -or $installer.Length -gt 2GB) {
    throw "The installer size is invalid."
}
$manifest = [ordered]@{
    schemaVersion = 1
    productId = "com.relaycove.client"
    platform = "win-x64"
    version = $Version
    buildNumber = $BuildNumber
    installerName = "RichChat-$Version-win-x64-Setup.exe"
    size = $installer.Length
    sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$target = [IO.Path]::GetFullPath($OutputPath)
$temporary = $target + "." + [Guid]::NewGuid().ToString("N") + ".partial"
try {
    [IO.File]::WriteAllText($temporary, ($manifest | ConvertTo-Json) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $target, $true)
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary }
}
