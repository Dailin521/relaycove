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
    Task<MessageQueryPage> SearchMessagesAsync(string query, long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Server message search is not available."));
    Task<MessageQueryPage> LoadSavedMessagesAsync(long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Saved messages are not available."));
    Task OpenMessageAsync(ConversationKey conversation, long messageId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Opening a message is not available."));
    Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default);
    Task SendAsync(string content, CancellationToken cancellationToken = default);
    Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default);
    Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default);
    Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default);
    Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default);
    Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChannelSummary>>([]);
    Task SubscribeToChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task SetSubscriptionPreferenceAsync(long channelId, SubscriptionPreference preference, bool value, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default);
    Task ClearLocalCacheAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
