namespace RelayCove.Client.Mentions;

internal static class ClientTextComposerContextPolicy
{
    public static bool ShouldClearCommittedDraft(
        bool pendingCommitted,
        Guid? submittedConversationId,
        Guid? currentConversationId,
        string submittedContent,
        string currentContent,
        bool replyContextUnchanged,
        bool mentionContextUnchanged) =>
        pendingCommitted &&
        submittedConversationId.HasValue &&
        submittedConversationId == currentConversationId &&
        replyContextUnchanged &&
        mentionContextUnchanged &&
        string.Equals(currentContent, submittedContent, StringComparison.Ordinal);
}
