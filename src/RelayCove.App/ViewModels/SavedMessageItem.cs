using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record SavedMessageItem(
    long MessageId,
    ConversationKey Conversation,
    string Sender,
    string Content,
    string Timestamp);
