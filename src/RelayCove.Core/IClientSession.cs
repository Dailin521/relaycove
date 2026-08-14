namespace RelayCove.Core;

public interface IClientSession
{
    AccountId? AccountId { get; }
    RealmEndpoint? ActiveRealm { get; }
    long? CurrentUserId { get; }
    long MaxFileUploadBytes { get; }
    ClientState State { get; }
    ConversationKey? SelectedConversation { get; }
    ConversationHistoryState HistoryState { get; }
    IReadOnlyList<ConversationKey> RecentDirectMessages { get; }
    event EventHandler<ClientStateChangedEventArgs>? StateChanged;
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);
    Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default);
    Task LoadOlderAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default);
    Task SendAsync(string content, CancellationToken cancellationToken = default);
    Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default);
    Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default);
    Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default);
    Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default);
    Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default);
    Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default);
    Task ClearLocalCacheAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
