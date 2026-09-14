[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$IsccPath,
    [switch]$RejectShutdownQuery
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$definition = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer/RichChat.iss'))
$settings = @{}
foreach ($name in @('CloseApplications', 'CloseApplicationsFilter', 'RestartApplications')) {
    $directiveMatches = [regex]::Matches($definition, "(?m)^$name=([^\r\n]+)")
    if ($directiveMatches.Count -ne 1) { throw "Expected one $name directive." }
    $settings[$name] = $directiveMatches[0].Groups[1].Value
}

# Exercise the real installer shutdown policy against disposable hidden processes.
# No product installation, shortcuts, uninstall registration, or user application is touched.
$fixtureRoot = Join-Path $repoRoot ('.verify/installer-close-tests/' + [Guid]::NewGuid().ToString('N'))
$targetRoot = Join-Path $fixtureRoot 'target'
$unrelatedRoot = Join-Path $fixtureRoot 'unrelated'
$payloadRoot = Join-Path $fixtureRoot 'payload'
foreach ($directory in @($targetRoot, $unrelatedRoot, $payloadRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$sourcePath = Join-Path $fixtureRoot 'StubbornTray.cs'
Set-Content -LiteralPath $sourcePath -Encoding utf8 -Value @'
using System;
using System.IO;
using System.Windows.Forms;

internal sealed class StubbornTray : Form
{
    private static bool rejectShutdownQuery;

    [STAThread]
    private static void Main(string[] args)
    {
        rejectShutdownQuery = args.Length > 1;
        using (var window = new StubbornTray())
        {
            var handle = window.Handle;
            File.WriteAllText(args[0], "ready");
            Application.Run();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0011) // WM_QUERYENDSESSION: optionally veto shutdown.
        {
            message.Result = new IntPtr(rejectShutdownQuery ? 0 : 1);
            return;
        }
        if (message.Msg == 0x0016 || message.Msg == 0x0010) // Keep the tray process alive.
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }
}
'@
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$helperPath = Join-Path $fixtureRoot 'RichChat.exe'
& $compiler /nologo /target:winexe /reference:System.Windows.Forms.dll "/out:$helperPath" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Could not build the isolated tray fixture.' }
Set-Content -LiteralPath (Join-Path $payloadRoot 'RichChat.exe') -Value 'updated fixture'

function Start-Fixture {
    param([string]$Directory)
    $executable = Join-Path $Directory 'RichChat.exe'
    Copy-Item -LiteralPath $helperPath -Destination $executable -Force
    $readyPath = Join-Path $Directory ([Guid]::NewGuid().ToString('N') + '.ready')
    $arguments = '"' + $readyPath + '"'
    if ($RejectShutdownQuery) { $arguments += ' reject' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $readyPath)) {
            if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Tray fixture did not become ready.' }
            Start-Sleep -Milliseconds 50
        }
        return $process
    }
    catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
        throw
    }
}

function Stop-Fixture {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    if (-not $Process.HasExited) { $Process.Kill(); $Process.WaitForExit() }
    $Process.Dispose()
}

$unrelated = $null
$target = $null
try {
    $unrelated = Start-Fixture $unrelatedRoot
    foreach ($mode in @('baseline', 'configured')) {
        $target = Start-Fixture $targetRoot
        $close = if ($mode -eq 'baseline') { 'yes' } else { $settings['CloseApplications'] }
        $scriptPath = Join-Path $fixtureRoot "$mode.iss"
        Set-Content -LiteralPath $scriptPath -Encoding utf8 -Value @"
[Setup]
AppId=RichChatCloseFixture-$([Guid]::NewGuid().ToString('N'))
AppName=RichChat Close Regression
AppVersion=1.0.0
DefaultDirName=$targetRoot
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
OutputDir=$fixtureRoot
OutputBaseFilename=$mode-setup
CloseApplications=$close
CloseApplicationsFilter=$($settings['CloseApplicationsFilter'])
RestartApplications=$($settings['RestartApplications'])
[Files]
Source: "$payloadRoot\RichChat.exe"; DestDir: "{app}"; Flags: ignoreversion
"@
        & $IsccPath /Q $scriptPath
        if ($LASTEXITCODE -ne 0) { throw "Could not compile $mode fixture." }
        $setup = Start-Process -FilePath (Join-Path $fixtureRoot "$mode-setup.exe") -WindowStyle Hidden -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $fixtureRoot "$mode.log") + '"'))
        try {
            if (-not $setup.WaitForExit(90000)) { $setup.Kill($true); throw "$mode fixture timed out." }
            $target.Refresh()
            $unrelated.Refresh()
            if ($unrelated.HasExited) { throw 'Installer closed an unrelated executable.' }
            if ($mode -eq 'baseline') {
                if ($setup.ExitCode -eq 0 -or $target.HasExited) { throw 'Baseline did not reproduce the shutdown failure.' }
                Write-Host 'PASS: normal shutdown leaves the stubborn tray process running and installation fails.'
            }
            else {
                if ($setup.ExitCode -ne 0 -or -not $target.HasExited) { throw 'Configured shutdown did not release the executable.' }
                if ([IO.File]::ReadAllText((Join-Path $targetRoot 'RichChat.exe')).Trim() -cne 'updated fixture') {
                    throw 'Installer did not replace the locked executable.'
                }
                Write-Host 'PASS: configured shutdown releases and replaces the executable; unrelated process stays running.'
            }
        }
        finally { $setup.Dispose() }
        Stop-Fixture $target
        $target = $null
    }
}
finally {
    Stop-Fixture $target
    Stop-Fixture $unrelated
    Write-Host "Installer close regression evidence: $fixtureRoot"
}
