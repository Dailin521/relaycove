namespace RelayCove.Core;

public interface IClientSession
{
    AccountId? AccountId { get; }
    RealmEndpoint? ActiveRealm { get; }
    long? CurrentUserId { get; }
    bool IsOrganizationAdministrator => false;
    bool CanCreatePrivateGroup => false;
    bool CanSetOwnPresence => false;
    UserPresenceStatus? OwnPresenceStatus => null;
    bool CanSetOwnUserStatus => false;
    UserStatusContent? OwnUserStatus => null;
    bool IsOwnUserStatusConfirmed => false;
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
    Task<MessageQueryPage> SearchMessagesAsync(
        string query,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default,
        MessageSearchFilter filter = MessageSearchFilter.Messages) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Server message search is not available."));
    Task<MessageQueryPage> LoadSavedMessagesAsync(long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Saved messages are not available."));
    Task OpenMessageAsync(ConversationKey conversation, long messageId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Opening a message is not available."));
    Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default);
    Task SetTopicVisibilityPolicyAsync(ChannelTopic topic, TopicVisibilityPolicy policy, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Topic visibility settings are not available."));
    Task MarkTopicReadAsync(ChannelTopic topic, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Topic read state is not available."));
    Task MoveTopicAsync(ChannelTopic source, ChannelTopic destination, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Moving topics is not available."));
    Task SetTopicResolvedAsync(ChannelTopic topic, bool isResolved, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Topic resolution is not available."));
    Task<TopicDeleteResult> DeleteTopicAsync(ChannelTopic topic, CancellationToken cancellationToken = default) => Task.FromException<TopicDeleteResult>(new NotSupportedException("Deleting topics is not available."));
    Task SendAsync(string content, CancellationToken cancellationToken = default);
    Task SendAsync(ConversationKey expectedConversation, string content, CancellationToken cancellationToken = default) =>
        string.Equals(SelectedConversation?.CanonicalKey, expectedConversation.CanonicalKey, StringComparison.Ordinal)
            ? SendAsync(content, cancellationToken)
            : Task.FromException(new InvalidOperationException("The selected conversation changed before send."));
    Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default);
    Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default);
    Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default);
    Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default);
    Task<RealmMediaDownloadResult> DownloadRealmMediaAsync(
        RealmMediaRequest request,
        Stream destination,
        IProgress<RealmMediaTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<RealmMediaDownloadResult>(new NotSupportedException("Streaming media downloads are not available."));
    Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChannelSummary>>([]);
    Task SubscribeToChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task SetSubscriptionPreferenceAsync(long channelId, SubscriptionPreference preference, bool value, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task SetOwnPresenceAsync(UserPresenceStatus status, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Presence settings are not available."));
    Task SetOwnUserStatusAsync(UserStatusContent status, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("User status settings are not available."));
    Task<ChannelSettingsSnapshot> LoadChannelSettingsSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromException<ChannelSettingsSnapshot>(new NotSupportedException("Channel settings are not available."));
    Task<ChannelDetails> LoadChannelDetailsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException<ChannelDetails>(new NotSupportedException("Channel settings are not available."));
    Task UpdateChannelAsync(long channelId, string? name, string? description, long? folderId, bool clearFolder = false, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task<ChannelFolder> CreateChannelFolderAsync(string name, string? description, CancellationToken cancellationToken = default) => Task.FromException<ChannelFolder>(new NotSupportedException("Channel settings are not available."));
    Task<string> GetChannelEmailAddressAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException<string>(new NotSupportedException("Channel settings are not available."));
    Task ArchiveChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task<ChannelSummary> CreateChannelAsync(ChannelCreateOptions options, CancellationToken cancellationToken = default) => Task.FromException<ChannelSummary>(new NotSupportedException("Channel creation is not available."));
    Task<PrivateGroupCreated> CreatePrivateGroupAsync(PrivateGroupCreateOptions options, CancellationToken cancellationToken = default) => Task.FromException<PrivateGroupCreated>(new NotSupportedException("Private-group creation is not available."));
    Task<ChannelPersonalSettings> GetChannelPersonalSettingsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException<ChannelPersonalSettings>(new NotSupportedException("Channel personal settings are not available."));
    Task SetChannelPersonalSettingAsync(long channelId, ChannelPersonalSettingChange change, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel personal settings are not available."));
    Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<UserProfile>>(new NotSupportedException("Realm users are not available."));
    Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<long>>(new NotSupportedException("Channel members are not available."));
    Task AddChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, bool sendNewSubscriptionMessages, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel members are not available."));
    Task RemoveChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel members are not available."));
    Task UpdateChannelAdvancedSettingsAsync(long channelId, ChannelAdvancedSettingsChange change, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task<PrivateGroupTransferResult> TransferPrivateGroupOwnershipAsync(long channelId, long newOwnerId, CancellationToken cancellationToken = default) => Task.FromException<PrivateGroupTransferResult>(new NotSupportedException("Private-group ownership transfer is not available."));
    Task<PrivateGroupDissolveResult> DissolvePrivateGroupAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException<PrivateGroupDissolveResult>(new NotSupportedException("Private-group dissolution is not available."));
    Task UnarchiveChannelAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default);
    Task MarkDisplayedReadAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default);
    Task ClearLocalCacheAsync(CancellationToken cancellationToken = default);
    Task ClearConversationCacheAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Conversation cache clearing is not available."));
    Task StopAsync(CancellationToken cancellationToken = default);
}
