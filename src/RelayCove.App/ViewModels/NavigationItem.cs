using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record NavigationItem(ConversationKey Conversation, string Title, string? Detail = null);
