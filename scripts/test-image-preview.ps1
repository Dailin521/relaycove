param(
    [Parameter(Mandatory = $true)][string]$AppBinaryDirectory,
    [ValidateSet('fixed', 'old', 'render-only', 'render-preview', 'events-only', 'clip-only', 'transform-only')]
    [string]$Mode = 'fixed',
    [ValidateRange(10, 120)][int]$TimeoutSeconds = 45,
    [switch]$ExpectCrash,
    [switch]$RunVisibleNativeTests
)

$ErrorActionPreference = 'Stop'
if (-not $RunVisibleNativeTests) {
    throw 'This test opens and repeatedly updates a visible native window. Run only with explicit current user authorization, then pass -RunVisibleNativeTests.'
}
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$binaryDirectory = (Resolve-Path -LiteralPath $AppBinaryDirectory).Path
$appAssembly = Join-Path $binaryDirectory 'RichChat.dll'
if (-not (Test-Path -LiteralPath $appAssembly)) { throw 'AppBinaryDirectory must contain RichChat.dll.' }
$runDirectory = Join-Path $workspace ('.verify/preview-native-' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$buildDirectory = Join-Path $runDirectory 'host'
$fixtureDirectory = Join-Path $runDirectory 'fixtures'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$project = Join-Path $workspace 'tests/RelayCove.Preview.NativeTests/RelayCove.Preview.NativeTests.csproj'
& dotnet build $project -c Debug --nologo "-p:AppBinaryDirectory=$binaryDirectory" "-p:BaseOutputPath=$buildDirectory/" -v:minimal 2>&1 |
    Tee-Object -FilePath (Join-Path $runDirectory 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Native preview host build failed.' }

$executable = Join-Path $buildDirectory 'Debug/net10.0-windows10.0.19041.0/win-x64/RelayCove.Preview.NativeTests.exe'
$start = [System.Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $executable
$start.Environment['PREVIEW_PROBE_MODE'] = $Mode
$start.Environment['PREVIEW_PROBE_OUTPUT'] = $fixtureDirectory
$startedAt = Get-Date
# A visible, independently named window is intentional: this is an explicitly
# requested native rendering test. No RichChat process is started or contacted.
$process = [System.Diagnostics.Process]::Start($start)
$timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) {
    # Retain the exact child process handle; never find or terminate by name.
    $process.Kill()
    $process.WaitForExit()
}
$result = [ordered]@{
    mode = $Mode
    processId = $process.Id
    exitCode = $process.ExitCode
    timedOut = $timedOut
    binaryDirectory = $binaryDirectory
    appSha256 = (Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash
    artifacts = $runDirectory
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json')
try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt } -ErrorAction Stop |
        Where-Object { $_.Id -in 1000, 1001 -and $_.Message -like '*RelayCove.Preview.NativeTests.exe*' } |
        Select-Object TimeCreated, Id, Message | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $runDirectory 'windows-events.json')
} catch { 'No matching Windows application events were available.' | Set-Content -LiteralPath (Join-Path $runDirectory 'windows-events.txt') }
$result | ConvertTo-Json
if ($timedOut) { throw 'Isolated native preview test timed out.' }
if ($ExpectCrash) {
    if ($process.ExitCode -eq 0) { throw 'Expected a baseline crash, but the process exited successfully.' }
} elseif ($process.ExitCode -ne 0) { throw "Isolated native preview test failed: $($process.ExitCode)." }
elseif (-not (Select-String -LiteralPath (Join-Path $fixtureDirectory 'phases.log') -Pattern ' PASS ' -Quiet)) {
    throw 'The native process exited without recording PASS.'
}
