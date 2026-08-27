namespace RelayCove.App.Services;

public sealed class MauiUiPreferencesService : IUiPreferencesService
{
    private const string DensityKey = "relaycove.ui.density";
    private const string FontScaleKey = "relaycove.ui.font-scale";
    private const string ConversationWidthKey = "relaycove.ui.conversation-width";
    private const string ChannelsExpandedKey = "relaycove.ui.channels-expanded";
    private const string DirectMessagesExpandedKey = "relaycove.ui.direct-messages-expanded";
    private const string FontSizeKey = "relaycove.ui.font-size";
    private const string ConversationPaneWidthKey = "relaycove.ui.conversation-pane-width";
    private const string ComposerHeightKey = "relaycove.ui.composer-height";

    public MauiUiPreferencesService()
    {
        Current = new UiPreferences(
            Parse(Preferences.Default.Get(DensityKey, nameof(UiDensityMode.Comfortable)), UiDensityMode.Comfortable),
            Parse(Preferences.Default.Get(FontScaleKey, nameof(UiFontScaleMode.Default)), UiFontScaleMode.Default),
            Parse(Preferences.Default.Get(ConversationWidthKey, nameof(UiConversationWidthMode.Standard)), UiConversationWidthMode.Standard),
            Preferences.Default.Get(ChannelsExpandedKey, true),
            Preferences.Default.Get(DirectMessagesExpandedKey, true),
            ReadRange(FontSizeKey, 11d, 18d),
            ReadRange(ConversationPaneWidthKey, 240d, 380d),
            ReadRange(ComposerHeightKey, 128d, 300d));
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
        Preferences.Default.Set(ChannelsExpandedKey, preferences.ChannelsExpanded);
        Preferences.Default.Set(DirectMessagesExpandedKey, preferences.DirectMessagesExpanded);
        if (preferences.FontSize is { } fontSize) Preferences.Default.Set(FontSizeKey, fontSize);
        else Preferences.Default.Remove(FontSizeKey);
        if (preferences.ConversationPaneWidth is { } paneWidth) Preferences.Default.Set(ConversationPaneWidthKey, paneWidth);
        else Preferences.Default.Remove(ConversationPaneWidthKey);
        if (preferences.ComposerHeight is { } composerHeight) Preferences.Default.Set(ComposerHeightKey, composerHeight);
        else Preferences.Default.Remove(ComposerHeightKey);
        Apply(preferences);
    }

    public void SaveComposerHeight(double height)
    {
        if (!double.IsFinite(height) || height < 128d || height > 300d)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Current = Current with { ComposerHeight = height };
        Preferences.Default.Set(ComposerHeightKey, height);
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

    private static double? ReadRange(string key, double minimum, double maximum)
    {
        var value = Preferences.Default.Get(key, double.NaN);
        return double.IsFinite(value) && value >= minimum && value <= maximum ? value : null;
    }

    private static void Apply(UiPreferences preferences)
    {
        if (Application.Current?.Resources is not { } resources) return;
        var baseFontSize = preferences.FontSize ?? (preferences.FontScale switch
        {
            UiFontScaleMode.Small => 12d,
            UiFontScaleMode.Large => 16d,
            _ => 14d
        });
        var fontScale = baseFontSize / 14d;
        resources["BaseFontSize"] = baseFontSize;
        resources["MutedFontSize"] = 12d * fontScale;
        resources["SmallFontSize"] = 11d * fontScale;
        resources["ButtonFontSize"] = 13d * fontScale;
        resources["ControlHeight"] = preferences.Density == UiDensityMode.Compact ? 32d : 36d;
        resources["ControlHorizontalPadding"] = new Thickness(
            preferences.Density == UiDensityMode.Compact ? 10d : 12d,
            0d);
    }
}
