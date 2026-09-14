namespace RelayCove.App.Services;

public sealed class MauiAppUpdatePreferences : IAppUpdatePreferences
{
    public bool CheckOnStartup
    {
        get => Preferences.Default.Get("relaycove.updates.check-on-startup", true);
        set => Preferences.Default.Set("relaycove.updates.check-on-startup", value);
    }

    public int LastPromptedBuildNumber
    {
        get => Preferences.Default.Get("relaycove.updates.last-prompted-build", 0);
        set => Preferences.Default.Set("relaycove.updates.last-prompted-build", value);
    }
}
