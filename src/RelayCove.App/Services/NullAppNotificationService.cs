namespace RelayCove.App.Services;

public sealed class NullAppNotificationService : IAppNotificationService
{
    public event EventHandler? StateChanged { add { } remove { } }
    public event EventHandler<AppNotificationActivatedEventArgs>? NotificationActivated { add { } remove { } }
    public bool IsSystemNotificationSupported => false;
    public string SystemNotificationStatus => "当前平台不支持系统通知。";
    public string TaskbarBadgeStatus => "当前平台不支持任务栏未读数量。";
    public void Attach(Window window) { }
    public void ShowMessageNotification(AppMessageNotification notification) { }
    public void UpdateTrayPreview(AppMessageNotification notification) { }
    public void UpdateTrayUnread(int count, bool isTruncated) { }
    public void UpdateUnreadBadge(int count, bool isTruncated) { }
    public void FlashTaskbar() { }
    public void StopTaskbarFlash() { }
    public void StopTrayFlash() { }
    public void Dispose() { }
}
