namespace RelayCove.App.Services;

public sealed record UiPreferences(
    UiDensityMode Density = UiDensityMode.Comfortable,
    UiFontScaleMode FontScale = UiFontScaleMode.Default,
    UiConversationWidthMode ConversationWidth = UiConversationWidthMode.Standard,
    bool OpenDetailsByDefault = false,
    bool ChannelsExpanded = true,
    bool DirectMessagesExpanded = true);
