namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentComposerContextPolicy
{
    public static bool IsCurrent(
        Guid? expectedConversationId,
        long expectedContextVersion,
        IReadOnlyList<Guid>? expectedDraftIds,
        Guid? currentConversationId,
        long currentContextVersion,
        IReadOnlyList<Guid>? currentDraftIds)
    {
        if (expectedDraftIds is null || currentDraftIds is null)
        {
            return false;
        }

        return expectedConversationId == currentConversationId &&
            expectedContextVersion == currentContextVersion &&
            expectedDraftIds.SequenceEqual(currentDraftIds);
    }

    public static bool CanApplyProgress(
        bool submissionRunning,
        long activeSubmissionVersion,
        long reportedSubmissionVersion,
        bool contextCurrent) =>
        submissionRunning &&
        activeSubmissionVersion == reportedSubmissionVersion &&
        contextCurrent;

    public static bool ShouldClearCommittedDraft(
        bool pendingCommitted,
        bool contextCurrent) =>
        pendingCommitted && contextCurrent;
}
