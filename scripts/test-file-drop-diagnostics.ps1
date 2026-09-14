#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetDirectoryName($PSScriptRoot)
$runRoot = Join-Path $repoRoot ('.verify/diagnostic-output-path/' + [Guid]::NewGuid().ToString('N'))
$windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$results = @()

# Exercise the same powershell.exe -File entry point used by the CMD launcher.
# A call via -Command evaluates default parameters in a different context in PS 5.1.
foreach ($scenario in @('Default', 'Explicit', 'Empty', 'Whitespace', 'Cmd')) {
    $caseRoot = Join-Path $runRoot ($scenario + ' space [' + [char]0x4E2D + [char]0x6587 + ']')
    [void][IO.Directory]::CreateDirectory($caseRoot)
    $scriptPath = Join-Path $caseRoot 'diagnose-file-drop.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnose-file-drop.ps1') -Destination $scriptPath
    $expectedRoot = $caseRoot
    $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $scriptPath + '"'
    switch ($scenario) {
        'Explicit' {
            $expectedRoot = Join-Path $caseRoot 'custom output'
            $arguments += ' -OutputDirectory "' + $expectedRoot + '"'
        }
        'Empty' { $arguments += ' -OutputDirectory ""' }
        'Whitespace' { $arguments += ' -OutputDirectory " "' }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $windowsPowerShell
    $startInfo.Arguments = $arguments
    if ($scenario -eq 'Cmd') {
        $launcherPath = Join-Path $caseRoot 'diagnose-file-drop.cmd'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnose-file-drop.cmd') -Destination $launcherPath
        $startInfo.FileName = Join-Path $env:SystemRoot 'System32/cmd.exe'
        $startInfo.Arguments = '/d /c ""' + $launcherPath + '""'
        $startInfo.RedirectStandardInput = $true
    }
    $startInfo.WorkingDirectory = $runRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if ($scenario -eq 'Cmd') {
            $process.StandardInput.WriteLine()
            $process.StandardInput.Close()
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "Diagnostic timed out: $scenario"
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stdout.log'), $stdout)
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stderr.log'), $stderr)
        $reports = @(Get-ChildItem -LiteralPath $caseRoot -Recurse -File -Filter report.json)
        $passed = $process.ExitCode -eq 0 -and $reports.Count -eq 1 -and [string]::IsNullOrWhiteSpace($stderr)
        if ($passed) {
            $report = [IO.File]::ReadAllText($reports[0].FullName) | ConvertFrom-Json
            $passed = $report.SchemaVersion -eq 1 -and
                $reports[0].Directory.Parent.FullName -eq $expectedRoot -and
                $stdout.Contains('Report saved: ')
        }
        $results += [pscustomobject]@{ Scenario = $scenario; Passed = $passed; ExitCode = $process.ExitCode }
    }
    finally { $process.Dispose() }
}

$results | Format-Table -AutoSize
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding UTF8
Write-Output "Evidence: $runRoot"
if (@($results | Where-Object { -not $_.Passed }).Count -gt 0) {
    throw 'File drop diagnostic regression checks failed.'
}
