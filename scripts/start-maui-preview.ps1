param(
    [ValidateSet('shell', 'shell-avatars', 'details', 'composer-empty', 'composer-uploading', 'composer-uploaded', 'composer-emoji', 'search-flow', 'message-quick-actions', 'message-menu', 'reaction-picker', 'account-menu', 'outbox-states', 'settings', 'narrow-list', 'narrow-chat', 'dm-cache-switch')]
    [string] $Scene = 'shell',
    [ValidateSet('light', 'dark', 'system')]
    [string] $Theme = 'light',
    [ValidateRange(480, 3840)]
    [int] $Width = 1440,
    [ValidateRange(560, 2160)]
    [int] $Height = 900,
    [string] $RunDirectory,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\RelayCove.App\RelayCove.App.csproj'
$standardExecutable = Join-Path $repoRoot 'src\RelayCove.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RichChat.exe'
$stateDirectory = if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    Join-Path $repoRoot (Join-Path 'artifacts\maui\runs' ("manual-{0}-{1}" -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [Guid]::NewGuid().ToString('N')))
} else {
    [IO.Path]::GetFullPath($RunDirectory, $repoRoot)
}
$processFile = Join-Path $stateDirectory 'preview-process.id'
$executableFile = Join-Path $stateDirectory 'preview-executable.path'
$executable = $standardExecutable

if (-not $NoBuild)
{
    $buildDirectory = Join-Path $stateDirectory (Join-Path 'preview-builds' ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')))
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    dotnet build $project -c Debug --no-restore --nologo -o $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'MAUI preview build failed; the existing preview was preserved.' }
    $executable = Join-Path $buildDirectory 'RichChat.exe'
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

New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$env:RELAYCOVE_NATIVE_UI_PREVIEW = '1'
$env:RELAYCOVE_NATIVE_UI_PREVIEW_SCENE = $Scene
$env:RELAYCOVE_NATIVE_UI_PREVIEW_THEME = $Theme
$env:RELAYCOVE_NATIVE_UI_PREVIEW_WIDTH = $Width.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:RELAYCOVE_NATIVE_UI_PREVIEW_HEIGHT = $Height.ToString([Globalization.CultureInfo]::InvariantCulture)
$preview = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
$preview.WaitForInputIdle(5000) | Out-Null
$preview.Refresh()
Set-Content -LiteralPath $processFile -Value $preview.Id -NoNewline
Set-Content -LiteralPath $executableFile -Value $executable -NoNewline
$runState = [ordered]@{
    ProcessId = $preview.Id
    ProcessStartTimeUtc = $preview.StartTime.ToUniversalTime().ToString('O')
    ProcessStartTimeUtcTicks = $preview.StartTime.ToUniversalTime().Ticks
    ExecutablePath = [IO.Path]::GetFullPath($executable)
    ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    Scene = $Scene
    Theme = $Theme
    DipWidth = $Width
    DipHeight = $Height
    RunDirectory = $stateDirectory
    Network = 'Disabled; NativeShellPreviewSession only.'
}
$runState | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stateDirectory 'preview-run.json') -Encoding utf8

[pscustomobject]@{
    ProcessId = $preview.Id
    ProcessStartTimeUtc = $runState.ProcessStartTimeUtc
    ExecutablePath = $runState.ExecutablePath
    ExecutableSha256 = $runState.ExecutableSha256
    Scene = $Scene
    Theme = $Theme
    DipSize = "$Width x $Height"
    DisplayPolicy = 'Debug preview selects a non-primary display when available.'
    Network = 'Disabled; NativeShellPreviewSession only.'
    RunDirectory = $stateDirectory
}
