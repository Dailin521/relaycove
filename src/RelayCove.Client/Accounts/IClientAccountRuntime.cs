using RelayCove.Client.Attachments;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountRuntime : IAsyncDisposable
{
    event Action<ConnectionState> ConnectionStateChanged;

    event Action<long> ConversationStateChanged;

    AccountScopeIdentity Identity { get; }

    ConnectionState ConnectionState { get; }

    bool TryAuthorizeNotificationTarget(ClientNotificationActivationTarget target);

    void UpdateActivity(ClientActivitySnapshot snapshot);

    Task<LocalConversationListReadOutcome> ReadConversationListAsync(
        CancellationToken cancellationToken = default);

    Task<LocalMessagePageReadOutcome> ReadMessagePageAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ClientMessageHistoryPageOutcome> LoadMessageHistoryAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ClientMessageAroundOutcome> LoadMessageAroundAsync(
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken = default);

    Task<ClientMentionCandidateOutcome> SearchMentionCandidatesAsync(
        Guid conversationId,
        string? query,
        int limit = ClientMentionCandidateCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default);

    Task<ClientSearchOutcome> SearchMessagesAsync(
        string? keyword,
        Guid? conversationId,
        int limit = ClientSearchCoordinator.DefaultLimit,
        CancellationToken cancellationToken = default);

    Task<ClientMessageSendOutcome> SendTextMessageAsync(
        Guid conversationId,
        string? content,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default);

    Task<ClientMessageSendOutcome> SendAttachmentsAsync(
        Guid conversationId,
        MessageType type,
        IReadOnlyList<ClientAttachmentUploadSource>? sources,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentSendProgress>? progress = null);

    Task<ClientMessageSendOutcome> RetryPendingMessageAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentDownloadOutcome> DownloadAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentDownloadProgress>? progress = null);

    Task<ClientAttachmentRevealOutcome> RevealAttachmentInFolderAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentRevealCommit commit,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentOpenOutcome> OpenAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        IntPtr ownerWindow,
        ClientAttachmentOpenCommit commit,
        CancellationToken cancellationToken = default);

    Task<ClientAttachmentImageLoadOutcome> LoadAttachmentImageAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentImageRendition rendition,
        ClientAttachmentImageCommit commit,
        CancellationToken cancellationToken = default);

    Task<LocalCacheOperationStatus> MarkConversationRenderedThroughAsync(
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken = default);

    Task<ClientAccountRuntimeStartOutcome> StartAsync(
        CancellationToken cancellationToken = default);

    Task<ClientSyncRunOutcome> RetryRealtimeAsync(
        CancellationToken cancellationToken = default);

    Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default);
}
