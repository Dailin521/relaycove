namespace RelayCove.App.Services;

public interface IUiPreferencesService
{
    UiPreferences Current { get; }
    void Save(UiPreferences preferences);
    void SaveComposerHeight(double height) => Save(Current with { ComposerHeight = height });
    UiPreferences Reset();
}
