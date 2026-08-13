using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record SearchResultItem(
    string Id,
    string Kind,
    string Title,
    string Subtitle,
    ConversationKey? Conversation = null,
    long? MessageId = null,
    long? ChannelId = null);
