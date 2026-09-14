#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell 5.1 -File can evaluate parameter defaults before PSScriptRoot is set.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $PSScriptRoot
}

# Query handles only. No process memory, window contents, command lines or credentials.
if (-not ('RichChatFileDropTokenProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class RichChatFileDropTokenProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static int[] Read(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) throw new Win32Exception();
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) throw new Win32Exception();
            int size;
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out size);
            if (size < IntPtr.Size) throw new Win32Exception();
            buffer = Marshal.AllocHGlobal(size);
            int needed;
            if (!GetTokenInformation(token, 25, buffer, size, out needed)) throw new Win32Exception();
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            int integrity = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            if (!GetTokenInformation(token, 20, buffer, 4, out needed)) throw new Win32Exception();
            return new int[] { integrity, Marshal.ReadInt32(buffer) };
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }
}
'@
}

function Read-RegistryValue([string]$Path, [string]$Name) {
    try { return Get-ItemPropertyValue -LiteralPath $Path -Name $Name -ErrorAction Stop }
    catch { return $null }
}

function Read-BinaryInfo([string]$Path) {
    try {
        $binary = Get-Item -LiteralPath $Path -ErrorAction Stop
        return [ordered]@{
            Status = 'Read'
            ProductVersion = $binary.VersionInfo.ProductVersion
            Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        }
    }
    catch { return [ordered]@{ Status = 'Unavailable' } }
}

$windowsKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$report = [ordered]@{
    SchemaVersion = 1
    CapturedUtc = [DateTime]::UtcNow.ToString('o')
    WindowsBuild = Read-RegistryValue $windowsKey 'CurrentBuildNumber'
    WindowsRevision = Read-RegistryValue $windowsKey 'UBR'
    Is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
    EnableLUA = Read-RegistryValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'EnableLUA'
    Processes = @()
    Observations = @()
}

$currentSessionId = (Get-Process -Id $PID).SessionId
foreach ($process in @(Get-Process -Name RichChat,explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $currentSessionId } | Sort-Object ProcessName,Id)) {
    $entry = [ordered]@{
        Name = $process.ProcessName
        Id = $process.Id
        TokenStatus = 'Unavailable'
        IntegrityLevel = $null
        Elevated = $null
    }
    try {
        $token = [RichChatFileDropTokenProbe]::Read($process.Id)
        $entry.TokenStatus = 'Read'
        $entry.IntegrityLevel = $token[0]
        $entry.Elevated = $token[1] -ne 0
    }
    catch { } # Access-denied or an exiting process is unknown, never "not elevated".

    if ($process.ProcessName -eq 'RichChat') {
        $entry.BinaryStatus = 'Unavailable'
        try {
            $appPath = $process.Path
            if (-not [string]::IsNullOrWhiteSpace($appPath)) {
                $entry.Executable = Read-BinaryInfo $appPath
                $entry.Assembly = Read-BinaryInfo (Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'RichChat.dll')
                $entry.BinaryStatus = 'Read'
            }
        }
        catch { }
    }
    $report.Processes += $entry
}

$apps = @($report.Processes | Where-Object { $_.Name -eq 'RichChat' })
$explorers = @($report.Processes | Where-Object { $_.Name -eq 'explorer' })
if ($apps.Count -eq 0) {
    $report.Observations += 'RichChat is not running in this Windows session. Run this check while the problem occurs.'
}
if ($null -eq $report.EnableLUA) {
    $report.Observations += 'UAC setting could not be read.'
}
elseif ($report.EnableLUA -eq 0) {
    $report.Observations += 'UAC is configured OFF. WinUI issue #10119 reports drag events missing in this configuration; this is a possible cause, not proof.'
}
foreach ($app in $apps) {
    if ($app.TokenStatus -ne 'Read') {
        $report.Observations += 'RichChat process permissions could not be read.'
        continue
    }
    if (@($explorers | Where-Object {
        $_.TokenStatus -eq 'Read' -and $_.IntegrityLevel -lt $app.IntegrityLevel
    }).Count -gt 0) {
        $report.Observations += 'RichChat has higher integrity than Explorer. Windows can block Explorer file drops across this boundary.'
    }
}
$report.Observations += 'This report does not test native drag events. Share report.json to compare with the working computer.'

$reportDirectory = Join-Path $OutputDirectory ('file-drop-diagnostics-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void][IO.Directory]::CreateDirectory($reportDirectory)
$reportPath = Join-Path $reportDirectory 'report.json'
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($reportPath, $json, [Text.UTF8Encoding]::new($false))
Write-Output $json
Write-Output "Report saved: $reportPath"
