using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsNotificationSoundPolicy
{
    private const string NotificationSettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    internal static bool IsSoundAllowed()
    {
        if (SHQueryUserNotificationState(out var userState) < 0 || userState != 5) return false;

        // Windows has no public API for the active Focus Assist profile. Query the
        // shell's state, including automatic rules; unknown layouts remain silent.
        // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED (Windows 10/11).
        var stateName = 0x0D83063EA3BF1C75UL;
        var size = sizeof(uint);
        var status = NtQueryWnfStateData(ref stateName, 0, 0, out _, out var profile, ref size);
        if (!IsUnrestrictedProfile(status, size, profile)) return false;

        using var globalSettings = Registry.CurrentUser.OpenSubKey(NotificationSettingsPath);
        if (!IsEnabledSetting(globalSettings?.GetValue("NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND"))) return false;

        var appId = GetNotificationAppId();
        if (string.IsNullOrWhiteSpace(appId)) return false;
        using var appSettings = Registry.CurrentUser.OpenSubKey($@"{NotificationSettingsPath}\{appId}");
        return IsApplicationSoundEnabled(appSettings?.GetValue("SoundFile"));
    }

    internal static bool IsUnrestrictedProfile(int status, int size, uint profile) =>
        status >= 0 && size == sizeof(uint) && profile == 0;

    // Windows omits default-on settings. Zero disables sound; unexpected types or
    // values must not override the user's mute choice. Registry access errors are
    // caught by the caller before any playback starts.
    internal static bool IsEnabledSetting(object? value) => value is null or 1;

    // The per-app switch is REG_SZ, unlike the global DWORD switch. Windows
    // represents the enabled default by an absent value or "*default*".
    internal static bool IsApplicationSoundEnabled(object? value) => value is null or "*default*";

    private static string? GetNotificationAppId()
    {
        var status = GetCurrentProcessExplicitAppUserModelID(out var appId);
        try
        {
            if (status >= 0 && appId != 0) return Marshal.PtrToStringUni(appId);
        }
        finally
        {
            if (appId != 0) Marshal.FreeCoTaskMem(appId);
        }

        // Match WinAppSDK RetrieveUnpackagedNotificationAppId. Register() creates
        // this mapping; sound playback only reads it and never changes OS settings.
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        using var registration = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\AppUserModelId\" + executablePath.Replace('\\', '.'));
        return registration?.GetValue("NotificationGUID") as string;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryWnfStateData(
        ref ulong stateName,
        nint typeId,
        nint explicitScope,
        out uint changeStamp,
        out uint buffer,
        ref int bufferSize);
}
