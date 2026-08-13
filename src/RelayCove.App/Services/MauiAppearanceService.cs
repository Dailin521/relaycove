namespace RelayCove.App.Services;

public sealed class MauiAppearanceService : IAppearanceService
{
    private const string PreferenceKey = "relaycove.appearance";

    public MauiAppearanceService()
    {
        Current = Parse(Preferences.Default.Get(PreferenceKey, nameof(AppAppearanceMode.System)));
        ApplyTheme(Current);
    }

    public AppAppearanceMode Current { get; private set; }

    public void Apply(AppAppearanceMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Current = mode;
        Preferences.Default.Set(PreferenceKey, mode.ToString());
        ApplyTheme(mode);
    }

    private static AppAppearanceMode Parse(string? value) =>
        Enum.TryParse<AppAppearanceMode>(value, ignoreCase: false, out var mode) && Enum.IsDefined(mode)
            ? mode
            : AppAppearanceMode.System;

    private static void ApplyTheme(AppAppearanceMode mode)
    {
        if (Application.Current is null) return;
        Application.Current.UserAppTheme = mode switch
        {
            AppAppearanceMode.Light => AppTheme.Light,
            AppAppearanceMode.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }
}
