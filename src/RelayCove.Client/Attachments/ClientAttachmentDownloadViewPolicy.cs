namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentDownloadViewPolicy
{
    public static bool IsCurrentContext(
        bool ready,
        ClientAttachmentDownloadContext expectedContext,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);

        return ready &&
            expectedContext.ConversationId == currentConversationId &&
            expectedContext.MessageClientId == currentMessageClientId &&
            expectedContext.AttachmentId == currentAttachmentId &&
            expectedContext.ContextVersion == currentContextVersion;
    }

    public static bool IsCurrent(
        bool ready,
        ClientAttachmentDownloadContext expectedContext,
        ClientAttachmentDownloadFlight expectedFlight,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion,
        ClientAttachmentDownloadFlight? activeFlight)
    {
        ArgumentNullException.ThrowIfNull(expectedFlight);

        return IsCurrentContext(
                ready,
                expectedContext,
                currentConversationId,
                currentMessageClientId,
                currentAttachmentId,
                currentContextVersion) &&
            ReferenceEquals(expectedFlight, activeFlight) &&
            ReferenceEquals(expectedContext, expectedFlight.Context);
    }
}
