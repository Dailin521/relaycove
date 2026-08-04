namespace RelayCove.Client.Sync;

using RelayCove.Client.Attachments;

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

    Task<ClientAttachmentOpenOutcome> OpenAsync(
        Guid conversationId,
        Guid attachmentId,
        IntPtr ownerWindow,
        ClientAttachmentOpenCommit commit,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentImageLoadOutcome> LoadImageAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentImageRendition rendition,
        ClientAttachmentImageCommit commit,
        CancellationToken cancellationToken = default);
}
