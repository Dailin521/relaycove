namespace RelayCove.Core;

public sealed record MessagePage(IReadOnlyList<ChatMessage> Messages, bool HasOlderInCache);
