namespace RelayCove.App.Services;

public interface IAppNotificationService : IDisposable
{
    event EventHandler? StateChanged;
    event EventHandler<AppNotificationActivatedEventArgs>? NotificationActivated;
    bool IsSystemNotificationSupported { get; }
    string SystemNotificationStatus { get; }
    string TaskbarBadgeStatus { get; }
    void Attach(Window window);
    void ShowMessageNotification(AppMessageNotification notification);
    void UpdateUnreadBadge(int count, bool isTruncated);
    void FlashTaskbar();
    void StopTaskbarFlash();
}
