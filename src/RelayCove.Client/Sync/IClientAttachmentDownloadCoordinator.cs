namespace RelayCove.Client.Sync;

internal interface IClientAttachmentDownloadCoordinator : IAsyncDisposable
{
    Task<ClientAttachmentCacheRecoveryStatus> RecoverAsync(
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentDownloadOutcome> DownloadAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentDownloadProgress>? progress = null);

    Task<ClientAttachmentRevealOutcome> RevealInFolderAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentRevealCommit commit,
        CancellationToken cancellationToken = default);
}
