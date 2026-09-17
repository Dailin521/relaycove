using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record SearchResultItem(
    string Id,
    string Kind,
    string Title,
    string Subtitle,
    ConversationKey? Conversation = null,
    long? MessageId = null,
    long? ChannelId = null,
    SearchContentKind ContentKinds = SearchContentKind.Message,
    string TimestampText = "")
{
    public IReadOnlyList<MessageAttachmentItem> Images { get; init; } = [];
    public bool HasImages => Images.Count > 0;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(TimestampText);
}
