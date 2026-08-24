using System.Text.Json;

namespace RelayCove.App.Services;

public sealed class MauiNotificationPreferencesService : INotificationPreferencesService
{
    private const string PreferenceKey = "relaycove.notification-preferences.v1";

    public MauiNotificationPreferencesService()
    {
        try
        {
            Current = JsonSerializer.Deserialize<NotificationPreferences>(
                Preferences.Default.Get(PreferenceKey, string.Empty)) ?? new();
        }
        catch (JsonException)
        {
            Current = new();
        }
    }

    public NotificationPreferences Current { get; private set; }

    public void Save(NotificationPreferences preferences)
    {
        Current = preferences ?? throw new ArgumentNullException(nameof(preferences));
        Preferences.Default.Set(PreferenceKey, JsonSerializer.Serialize(preferences));
    }
}
