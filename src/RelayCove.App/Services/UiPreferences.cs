namespace RelayCove.App.Services;

public sealed record UiPreferences(
    UiDensityMode Density = UiDensityMode.Comfortable,
    UiFontScaleMode FontScale = UiFontScaleMode.Default,
    UiConversationWidthMode ConversationWidth = UiConversationWidthMode.Standard,
    bool ChannelsExpanded = true,
    bool DirectMessagesExpanded = true,
    double? FontSize = null,
    double? ConversationPaneWidth = null);
