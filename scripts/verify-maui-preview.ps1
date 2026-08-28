param(
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\RelayCove.App\RelayCove.App.csproj'
$runId = "{0}-{1}" -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repoRoot (Join-Path 'artifacts\maui\runs' $runId)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not ([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary }))
{
    throw 'A non-primary display is required; preview acceptance will not fall back to the primary display.'
}

if (-not $NoBuild)
{
    dotnet build $project -c Debug --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Debug MAUI build failed.' }
}

$matrix = @(
    @{ Scene = 'shell-avatars'; Width = 1440; Height = 900; Theme = 'light' },
    @{ Scene = 'narrow-list'; Width = 640; Height = 900; Theme = 'light' },
    @{ Scene = 'account-menu'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'account-menu'; Width = 1024; Height = 768; Theme = 'dark' },
    @{ Scene = 'composer-empty'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'composer-empty'; Width = 1024; Height = 768; Theme = 'dark' },
    @{ Scene = 'composer-uploading'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'composer-uploading'; Width = 1024; Height = 768; Theme = 'dark' },
    @{ Scene = 'composer-uploaded'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'composer-uploaded'; Width = 1024; Height = 768; Theme = 'dark' },
    @{ Scene = 'search-flow'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'message-quick-actions'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'message-menu'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'outbox-states'; Width = 1024; Height = 768; Theme = 'light' },
    @{ Scene = 'outbox-states'; Width = 1024; Height = 768; Theme = 'dark' }
)

function Get-ElementsByAutomationId([Windows.Automation.AutomationElement] $root, [string] $automationId)
{
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) | ForEach-Object { $_ })
}

function Get-ButtonsByName([Windows.Automation.AutomationElement] $root, [string] $name)
{
    $button = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $named = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $name)
    $condition = [Windows.Automation.AndCondition]::new($button, $named)
    return @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) | ForEach-Object { $_ })
}

function Add-Assertion([System.Collections.Generic.List[object]] $assertions, [string] $name, [bool] $passed, [string] $actual)
{
    $assertions.Add([pscustomobject]@{ Name = $name; Passed = $passed; Actual = $actual })
    if (-not $passed) { throw "UIA assertion failed: $name ($actual)" }
}

function Test-SceneUia([Diagnostics.Process] $process, [string] $scene)
{
    $root = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if (-not $root) { throw 'UI Automation could not resolve the tracked preview HWND.' }
    $assertions = [System.Collections.Generic.List[object]]::new()

    switch ($scene)
    {
        'account-menu' {
            $summary = Get-ElementsByAutomationId $root 'AccountStatusSummary'
            Add-Assertion $assertions 'readonly status summary exists' ($summary.Count -eq 1) "count=$($summary.Count)"
            foreach ($setterName in @('在线', '忙碌', '离线', '清除个人状态'))
            {
                $buttons = Get-ButtonsByName $root $setterName
                Add-Assertion $assertions "no status setter button: $setterName" ($buttons.Count -eq 0) "count=$($buttons.Count)"
            }
        }
        'composer-empty' {
            $send = Get-ElementsByAutomationId $root 'ComposerSendButton'
            Add-Assertion $assertions 'empty composer send disabled' ($send.Count -eq 1 -and -not $send[0].Current.IsEnabled) "count=$($send.Count) enabled=$(if ($send.Count) {$send[0].Current.IsEnabled})"
        }
        'composer-uploading' {
            $send = Get-ElementsByAutomationId $root 'ComposerSendButton'
            $progress = Get-ElementsByAutomationId $root 'AttachmentUploadProgress'
            Add-Assertion $assertions 'uploading composer send disabled' ($send.Count -eq 1 -and -not $send[0].Current.IsEnabled) "count=$($send.Count) enabled=$(if ($send.Count) {$send[0].Current.IsEnabled})"
            Add-Assertion $assertions 'upload progress exists' ($progress.Count -ge 1) "count=$($progress.Count)"
        }
        'composer-uploaded' {
            $send = Get-ElementsByAutomationId $root 'ComposerSendButton'
            Add-Assertion $assertions 'uploaded-only composer send enabled' ($send.Count -eq 1 -and $send[0].Current.IsEnabled) "count=$($send.Count) enabled=$(if ($send.Count) {$send[0].Current.IsEnabled})"
        }
        'search-flow' {
            $entry = Get-ElementsByAutomationId $root 'SearchEntry'
            $empty = Get-ElementsByAutomationId $root 'SearchEmptyState'
            Add-Assertion $assertions 'search entry exists' ($entry.Count -eq 1) "count=$($entry.Count)"
            Add-Assertion $assertions 'search entry focused' ($entry.Count -eq 1 -and $entry[0].Current.HasKeyboardFocus) "focused=$(if ($entry.Count) {$entry[0].Current.HasKeyboardFocus})"
            Add-Assertion $assertions 'empty-search guidance exists' ($empty.Count -eq 1) "count=$($empty.Count)"
        }
        'message-quick-actions' {
            $edit = Get-ElementsByAutomationId $root 'MessageEditButton'
            $more = Get-ElementsByAutomationId $root 'MessageMoreButton'
            Add-Assertion $assertions 'only own quick bar exposes edit' ($edit.Count -eq 1) "count=$($edit.Count)"
            Add-Assertion $assertions 'own and other quick bars expose more' ($more.Count -ge 2) "count=$($more.Count)"
        }
        'message-menu' {
            $quote = Get-ElementsByAutomationId $root 'QuoteMessageMenuItem'
            Add-Assertion $assertions 'full menu retains quote' ($quote.Count -eq 1) "count=$($quote.Count)"
        }
        'outbox-states' {
            $states = Get-ElementsByAutomationId $root 'MessageDeliveryState'
            $recover = Get-ElementsByAutomationId $root 'RecoverOutboxButton'
            Add-Assertion $assertions 'only failure and unknown states are announced' ($states.Count -eq 2) "count=$($states.Count)"
            Add-Assertion $assertions 'failure and unknown states are recoverable' ($recover.Count -eq 2) "count=$($recover.Count)"
        }
    }

    return @($assertions)
}

function Stop-TrackedPreview([string] $sceneRunDirectory)
{
    $state = Get-Content -LiteralPath (Join-Path $sceneRunDirectory 'preview-run.json') -Raw | ConvertFrom-Json
    $process = Get-Process -Id ([int]$state.ProcessId) -ErrorAction SilentlyContinue
    if (-not $process) { return }
    if (-not [string]::Equals($process.Path, [string]$state.ExecutablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup refused: executable path mismatch.' }
    if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$state.ProcessStartTimeUtcTicks) { throw 'Cleanup refused: process start time mismatch.' }
    if (-not [string]::Equals((Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash, [string]$state.ExecutableSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup refused: executable hash mismatch.' }
    Stop-Process -Id $process.Id -Force
    Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
}

function Assert-TrackedPreviewAfterCapture([Diagnostics.Process] $process, [string] $sceneRunDirectory, [string] $evidencePath)
{
    $state = Get-Content -LiteralPath (Join-Path $sceneRunDirectory 'preview-run.json') -Raw | ConvertFrom-Json
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $process.Refresh()
    if ($process.HasExited -or $process.Id -ne [int]$state.ProcessId) { throw 'UIA refused: tracked preview exited or PID changed.' }
    if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$state.ProcessStartTimeUtcTicks) { throw 'UIA refused: process start time mismatch.' }
    if (-not [string]::Equals($process.Path, [string]$state.ExecutablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'UIA refused: executable path mismatch.' }
    if (-not [string]::Equals((Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash, [string]$state.ExecutableSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'UIA refused: executable hash mismatch.' }
    $screen = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle)
    if ($screen.Primary -or -not [string]::Equals($screen.DeviceName, [string]$evidence.Screen, [StringComparison]::OrdinalIgnoreCase)) { throw 'UIA refused: HWND is not on the captured non-primary display.' }
}

$results = [System.Collections.Generic.List[object]]::new()
try
{
    for ($index = 0; $index -lt $matrix.Count; $index++)
    {
        $item = $matrix[$index]
        $sceneKey = '{0:D2}-{1}-{2}' -f ($index + 1), $item.Scene, $item.Theme
        $sceneRunDirectory = Join-Path $runRoot $sceneKey
        New-Item -ItemType Directory -Path $sceneRunDirectory -Force | Out-Null
        try
        {
            $started = & (Join-Path $PSScriptRoot 'start-maui-preview.ps1') -Scene $item.Scene -Theme $item.Theme -Width $item.Width -Height $item.Height -RunDirectory $sceneRunDirectory -NoBuild
            $process = Get-Process -Id $started.ProcessId -ErrorAction Stop
            for ($attempt = 0; $attempt -lt 60 -and $process.MainWindowHandle -eq [IntPtr]::Zero; $attempt++)
            {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
            }
            Start-Sleep -Milliseconds 900
            $process.Refresh()
            $imagePath = Join-Path $sceneRunDirectory "$sceneKey.png"
            $capture = & (Join-Path $PSScriptRoot 'capture-maui-preview.ps1') -OutputPath $imagePath -RunDirectory $sceneRunDirectory -DipWidth $item.Width -DipHeight $item.Height
            $evidencePath = [IO.Path]::ChangeExtension($capture.OutputPath, '.evidence.json')
            Assert-TrackedPreviewAfterCapture $process $sceneRunDirectory $evidencePath
            $uia = Test-SceneUia $process $item.Scene
            $results.Add([pscustomobject]@{
                Scene = $item.Scene
                Theme = $item.Theme
                Passed = $true
                UiaAssertions = $uia
                EvidencePath = $evidencePath
                ScreenshotPath = $capture.OutputPath
                ScreenshotSha256 = $capture.Sha256
            })
        }
        finally
        {
            if (Test-Path -LiteralPath (Join-Path $sceneRunDirectory 'preview-run.json')) { Stop-TrackedPreview $sceneRunDirectory }
        }
    }
}
catch
{
    $report = [ordered]@{
        RunId = $runId
        Passed = $false
        Live = 'not run'
        Error = $_.Exception.Message
        Results = @($results)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'report.json') -Encoding utf8
    throw
}

$report = [ordered]@{
    RunId = $runId
    Passed = $true
    Live = 'not run'
    SecondaryDisplayRequired = $true
    Results = @($results)
}
$reportPath = Join-Path $runRoot 'report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]@{ RunId = $runId; Passed = $true; ReportPath = $reportPath; ScreenshotCount = $results.Count }
