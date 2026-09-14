using Microsoft.Win32;
using RelayCove.App.Services;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsStartupService : IStartupService
{
    internal const string ValueName = "RichChat";
    private readonly string _executablePath;
    private readonly string _runKeyPath;
    private readonly string _approvalKeyPath;

    public WindowsStartupService() : this(
        Path.Combine(AppContext.BaseDirectory, "RichChat.exe"),
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
    {
    }

    internal WindowsStartupService(string executablePath, string runKeyPath, string approvalKeyPath)
    {
        _executablePath = executablePath;
        _runKeyPath = runKeyPath;
        _approvalKeyPath = approvalKeyPath;
    }

    public StartupState GetState()
    {
        // Merely launching the app or opening Settings must never register it.
        using var runKey = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        if (runKey?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command))
        {
            return StartupState.Disabled;
        }
        if (!string.Equals(command, GetCommand(), StringComparison.OrdinalIgnoreCase))
            return StartupState.DifferentExecutable;

        // Windows owns this approval. Read it to explain Task Manager overrides;
        // never clear or rewrite it to bypass a user's system-level choice.
        using var approvalKey = Registry.CurrentUser.OpenSubKey(_approvalKeyPath);
        var approval = approvalKey?.GetValue(ValueName);
        if (approval is null) return StartupState.Enabled;
        if (approval is not byte[] bytes || bytes.Length < 12)
            return StartupState.UnknownWindowsApproval;

        return BitConverter.ToUInt32(bytes, 0) switch
        {
            2 or 6 => StartupState.Enabled,
            3 or 7 => StartupState.DisabledByWindows,
            _ => StartupState.UnknownWindowsApproval
        };
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var command = GetCommand();
            // Run entries are limited to 260 characters, including quotes.
            if (!Path.IsPathFullyQualified(_executablePath) || _executablePath.Contains('"') ||
                command.Length > 260 || !File.Exists(_executablePath))
            {
                throw new InvalidOperationException("The startup executable is unavailable.");
            }

            using var runKey = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            runKey.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private string GetCommand() => $"\"{_executablePath}\"";
}
