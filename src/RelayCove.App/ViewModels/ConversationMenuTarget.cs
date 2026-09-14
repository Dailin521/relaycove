using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record ConversationMenuTarget(AccountId AccountId, ConversationKey Conversation, bool IsPinned, bool IsMuted);
