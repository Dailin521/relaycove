param(
    [Parameter(Mandatory)]
    [string] $OutputPath,
    [Parameter(Mandatory)]
    [string] $RunDirectory,
    [ValidateRange(480, 3840)]
    [int] $DipWidth = 1440,
    [ValidateRange(560, 2160)]
    [int] $DipHeight = 900
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = [IO.Path]::GetFullPath($RunDirectory, $repoRoot)
$processFile = Join-Path $stateDirectory 'preview-process.id'
$executableFile = Join-Path $stateDirectory 'preview-executable.path'
$runFile = Join-Path $stateDirectory 'preview-run.json'

if (-not (Test-Path -LiteralPath $processFile) -or -not (Test-Path -LiteralPath $executableFile) -or -not (Test-Path -LiteralPath $runFile))
{
    throw 'No RichChat preview process is recorded. Run scripts/start-maui-preview.ps1 first.'
}

$previewProcessId = 0
if (-not [int]::TryParse((Get-Content -LiteralPath $processFile -Raw).Trim(), [ref] $previewProcessId))
{
    throw 'The recorded RichChat preview process ID is invalid.'
}

$expectedExecutable = (Get-Content -LiteralPath $executableFile -Raw).Trim()
$runState = Get-Content -LiteralPath $runFile -Raw | ConvertFrom-Json
$process = Get-Process -Id $previewProcessId -ErrorAction Stop
if (-not [string]::Equals($process.Path, $expectedExecutable, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'The recorded process is not the RichChat preview executable started by this worktree.'
}
if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$runState.ProcessStartTimeUtcTicks)
{
    throw 'The recorded PID was reused by a process with a different start time.'
}
$currentExecutableHash = (Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash
if (-not [string]::Equals($currentExecutableHash, [string]$runState.ExecutableSha256, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'The tracked preview executable hash changed after launch.'
}

for ($attempt = 0; $attempt -lt 50 -and $process.MainWindowHandle -eq [IntPtr]::Zero; $attempt++)
{
    Start-Sleep -Milliseconds 100
    $process.Refresh()
}
if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'RichChat preview did not create a window.' }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RelayCoveTrackedPreviewCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect value);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect value, int size);
}
'@

[RelayCoveTrackedPreviewCapture]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screens = [System.Windows.Forms.Screen]::AllScreens
$targetScreen = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $targetScreen) { throw 'A non-primary display is required for autonomous preview acceptance.' }

# The HWND can still carry the primary monitor's DPI during startup. Move it to
# the target display without resizing, then query the per-monitor DPI used for
# the requested DIP dimensions. This avoids a race with the preview adapter's
# delayed secondary-display placement.
if (-not [RelayCoveTrackedPreviewCapture]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        $targetScreen.WorkingArea.Left,
        $targetScreen.WorkingArea.Top,
        0,
        0,
        0x0015))
{
    throw "Initial secondary-display placement failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}
# The app's own preview placement timer fires after one second. Let that
# complete before calculating the final capture rectangle, then the harness is
# the last component to size the tracked HWND.
Start-Sleep -Milliseconds 1500
$dpi = [RelayCoveTrackedPreviewCapture]::GetDpiForWindow($process.MainWindowHandle)
$pixelWidth = [Math]::Round($DipWidth * $dpi / 96)
$pixelHeight = [Math]::Round($DipHeight * $dpi / 96)
$rect = [RelayCoveTrackedPreviewCapture+Rect]::new()
$outerWidth = $pixelWidth
$outerHeight = $pixelHeight
for ($attempt = 0; $attempt -lt 3; $attempt++)
{
    $x = $targetScreen.WorkingArea.Left + [Math]::Max(0, [Math]::Floor(($targetScreen.WorkingArea.Width - $outerWidth) / 2))
    $y = $targetScreen.WorkingArea.Top + [Math]::Max(0, [Math]::Floor(($targetScreen.WorkingArea.Height - $outerHeight) / 2))
    if (-not [RelayCoveTrackedPreviewCapture]::SetWindowPos(
            $process.MainWindowHandle,
            [IntPtr]::Zero,
            $x,
            $y,
            $outerWidth,
            $outerHeight,
            0x0054))
    {
        throw "SetWindowPos failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }

    Start-Sleep -Milliseconds 450
    $dwmResult = [RelayCoveTrackedPreviewCapture]::DwmGetWindowAttribute(
        $process.MainWindowHandle,
        9,
        [ref] $rect,
        [Runtime.InteropServices.Marshal]::SizeOf([type][RelayCoveTrackedPreviewCapture+Rect]))
    if ($dwmResult -ne 0) { throw "DwmGetWindowAttribute failed: $dwmResult" }
    $actualWidth = $rect.Right - $rect.Left
    $actualHeight = $rect.Bottom - $rect.Top
    if ($actualWidth -eq $pixelWidth -and $actualHeight -eq $pixelHeight) { break }
    $outerWidth += $pixelWidth - $actualWidth
    $outerHeight += $pixelHeight - $actualHeight
}

Start-Sleep -Milliseconds 350
$dwmResult = [RelayCoveTrackedPreviewCapture]::DwmGetWindowAttribute(
    $process.MainWindowHandle,
    9,
    [ref] $rect,
    [Runtime.InteropServices.Marshal]::SizeOf([type][RelayCoveTrackedPreviewCapture+Rect]))
if ($dwmResult -ne 0) { throw "DwmGetWindowAttribute failed: $dwmResult" }

$actualWidth = $rect.Right - $rect.Left
$actualHeight = $rect.Bottom - $rect.Top
if ([Math]::Abs($actualWidth - $pixelWidth) -gt 1 -or [Math]::Abs($actualHeight - $pixelHeight) -gt 1)
{
    throw "Preview window did not converge within +/-1 px. Expected $pixelWidth x $pixelHeight; actual $actualWidth x $actualHeight."
}
$finalScreen = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle)
if ($finalScreen.Primary -or -not [string]::Equals($finalScreen.DeviceName, $targetScreen.DeviceName, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Preview HWND is not on the required non-primary display. Expected $($targetScreen.DeviceName); actual $($finalScreen.DeviceName)."
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath, $repoRoot)
$directory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$bitmap = [System.Drawing.Bitmap]::new($actualWidth, $actualHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$deviceContext = $graphics.GetHdc()
try
{
    if (-not [RelayCoveTrackedPreviewCapture]::PrintWindow($process.MainWindowHandle, $deviceContext, 2))
    {
        throw 'PrintWindow failed.'
    }
}
finally
{
    $graphics.ReleaseHdc($deviceContext)
    $graphics.Dispose()
}
$bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$evidence = [ordered]@{
    ProcessId = $previewProcessId
    ProcessStartTimeUtc = $runState.ProcessStartTimeUtc
    ExecutablePath = $process.Path
    ExecutableSha256 = $currentExecutableHash
    Scene = $runState.Scene
    Theme = $runState.Theme
    Dpi = $dpi
    DipWidth = $DipWidth
    DipHeight = $DipHeight
    PixelWidth = $actualWidth
    PixelHeight = $actualHeight
    Screen = $finalScreen.DeviceName
    ScreenPrimary = $finalScreen.Primary
    WindowRect = [ordered]@{ Left = $rect.Left; Top = $rect.Top; Right = $rect.Right; Bottom = $rect.Bottom }
    OutputPath = $resolvedOutput
    Sha256 = (Get-FileHash $resolvedOutput -Algorithm SHA256).Hash
    InputAutomation = 'None'
}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($resolvedOutput, '.evidence.json')) -Encoding utf8
[pscustomobject]$evidence
