using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsProcessEnvironment
{
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 20,
            out var elevated, sizeof(int), out _) && elevated != 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token, int informationClass, out int information, int length, out int returnLength);
}
