namespace RelayCove.Client.Desktop;

internal interface IWindowsDesktopAttentionNative
{
    bool PlayNotificationSound();

    void StartTaskbarFlash(nint windowHandle);

    void StopTaskbarFlash(nint windowHandle);
}
