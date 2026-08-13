namespace RelayCove.App.Services;

public interface IUiPreferencesService
{
    UiPreferences Current { get; }
    void Save(UiPreferences preferences);
    UiPreferences Reset();
}
