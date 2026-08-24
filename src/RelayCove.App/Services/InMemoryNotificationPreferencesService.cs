namespace RelayCove.App.Services;

public sealed class InMemoryNotificationPreferencesService : INotificationPreferencesService
{
    public NotificationPreferences Current { get; private set; } = new();

    public void Save(NotificationPreferences preferences) =>
        Current = preferences ?? throw new ArgumentNullException(nameof(preferences));
}
