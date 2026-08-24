namespace RelayCove.App.Services;

public interface INotificationPreferencesService
{
    NotificationPreferences Current { get; }
    void Save(NotificationPreferences preferences);
}
