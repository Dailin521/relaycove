param(
    [ValidateSet('shell', 'details', 'composer-emoji', 'message-menu', 'reaction-picker', 'account-menu', 'settings', 'narrow-list', 'narrow-chat', 'dm-cache-switch')]
    [string] $Scene = 'shell',
    [ValidateSet('light', 'dark', 'system')]
    [string] $Theme = 'light',
    [ValidateRange(480, 3840)]
    [int] $Width = 1440,
    [ValidateRange(560, 2160)]
    [int] $Height = 900,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\RelayCove.App\RelayCove.App.csproj'
$standardExecutable = Join-Path $repoRoot 'src\RelayCove.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RelayCove.App.exe'
$stateDirectory = Join-Path $repoRoot 'artifacts\maui'
$processFile = Join-Path $stateDirectory 'preview-process.id'
$executableFile = Join-Path $stateDirectory 'preview-executable.path'
$executable = $standardExecutable

if (-not $NoBuild)
{
    $buildDirectory = Join-Path $stateDirectory (Join-Path 'preview-builds' ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')))
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    dotnet build $project -c Debug --no-restore --nologo -o $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'MAUI preview build failed; the existing preview was preserved.' }
    $executable = Join-Path $buildDirectory 'RelayCove.App.exe'
}
elseif (Test-Path -LiteralPath $executableFile)
{
    $recordedExecutable = (Get-Content -LiteralPath $executableFile -Raw).Trim()
    if (Test-Path -LiteralPath $recordedExecutable) { $executable = $recordedExecutable }
}

if (-not (Test-Path -LiteralPath $executable))
{
    throw "Preview executable does not exist: $executable"
}

if (Test-Path -LiteralPath $processFile)
{
    $previousProcessId = 0
    if ([int]::TryParse((Get-Content -LiteralPath $processFile -Raw).Trim(), [ref] $previousProcessId))
    {
        $previous = Get-Process -Id $previousProcessId -ErrorAction SilentlyContinue
        $previousExecutable = if (Test-Path -LiteralPath $executableFile) {
            (Get-Content -LiteralPath $executableFile -Raw).Trim()
        } else {
            $standardExecutable
        }
        if ($previous -and [string]::Equals($previous.Path, $previousExecutable, [StringComparison]::OrdinalIgnoreCase))
        {
            Stop-Process -Id $previousProcessId -Force
            Wait-Process -Id $previousProcessId -ErrorAction SilentlyContinue
        }
    }
}

New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$env:RELAYCOVE_NATIVE_UI_PREVIEW = '1'
$env:RELAYCOVE_NATIVE_UI_PREVIEW_SCENE = $Scene
$env:RELAYCOVE_NATIVE_UI_PREVIEW_THEME = $Theme
$env:RELAYCOVE_NATIVE_UI_PREVIEW_WIDTH = $Width.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:RELAYCOVE_NATIVE_UI_PREVIEW_HEIGHT = $Height.ToString([Globalization.CultureInfo]::InvariantCulture)
$preview = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
Set-Content -LiteralPath $processFile -Value $preview.Id -NoNewline
Set-Content -LiteralPath $executableFile -Value $executable -NoNewline

[pscustomobject]@{
    ProcessId = $preview.Id
    Scene = $Scene
    Theme = $Theme
    DipSize = "$Width x $Height"
    DisplayPolicy = 'Debug preview selects a non-primary display when available.'
    Network = 'Disabled; NativeShellPreviewSession only.'
}
