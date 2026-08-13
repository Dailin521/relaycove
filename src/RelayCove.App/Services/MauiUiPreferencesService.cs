namespace RelayCove.App.Services;

public sealed class MauiUiPreferencesService : IUiPreferencesService
{
    private const string DensityKey = "relaycove.ui.density";
    private const string FontScaleKey = "relaycove.ui.font-scale";
    private const string ConversationWidthKey = "relaycove.ui.conversation-width";
    private const string DetailsDefaultKey = "relaycove.ui.details-default";
    private const string ChannelsExpandedKey = "relaycove.ui.channels-expanded";
    private const string DirectMessagesExpandedKey = "relaycove.ui.direct-messages-expanded";

    public MauiUiPreferencesService()
    {
        Current = new UiPreferences(
            Parse(Preferences.Default.Get(DensityKey, nameof(UiDensityMode.Comfortable)), UiDensityMode.Comfortable),
            Parse(Preferences.Default.Get(FontScaleKey, nameof(UiFontScaleMode.Default)), UiFontScaleMode.Default),
            Parse(Preferences.Default.Get(ConversationWidthKey, nameof(UiConversationWidthMode.Standard)), UiConversationWidthMode.Standard),
            Preferences.Default.Get(DetailsDefaultKey, false),
            Preferences.Default.Get(ChannelsExpandedKey, true),
            Preferences.Default.Get(DirectMessagesExpandedKey, true));
        Apply(Current);
    }

    public UiPreferences Current { get; private set; }

    public void Save(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.Density) ||
            !Enum.IsDefined(preferences.FontScale) ||
            !Enum.IsDefined(preferences.ConversationWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(preferences));
        }

        Current = preferences;
        Preferences.Default.Set(DensityKey, preferences.Density.ToString());
        Preferences.Default.Set(FontScaleKey, preferences.FontScale.ToString());
        Preferences.Default.Set(ConversationWidthKey, preferences.ConversationWidth.ToString());
        Preferences.Default.Set(DetailsDefaultKey, preferences.OpenDetailsByDefault);
        Preferences.Default.Set(ChannelsExpandedKey, preferences.ChannelsExpanded);
        Preferences.Default.Set(DirectMessagesExpandedKey, preferences.DirectMessagesExpanded);
        Apply(preferences);
    }

    public UiPreferences Reset()
    {
        var preferences = new UiPreferences();
        Save(preferences);
        return preferences;
    }

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private static void Apply(UiPreferences preferences)
    {
        if (Application.Current?.Resources is not { } resources) return;
        var fontScale = preferences.FontScale switch
        {
            UiFontScaleMode.Small => 0.9d,
            UiFontScaleMode.Large => 1.15d,
            _ => 1d
        };
        resources["BaseFontSize"] = 14d * fontScale;
        resources["MutedFontSize"] = 12d * fontScale;
        resources["SmallFontSize"] = 11d * fontScale;
        resources["ButtonFontSize"] = 13d * fontScale;
        resources["ControlHeight"] = preferences.Density == UiDensityMode.Compact ? 32d : 36d;
        resources["ControlHorizontalPadding"] = new Thickness(
            preferences.Density == UiDensityMode.Compact ? 10d : 12d,
            0d);
    }
}
