namespace RelayCove.Core;

public interface IZulipGateway
{
    Task<RealmProbeResult> ProbeRealmAsync(RealmEndpoint realm, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default);
    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<RealmPresenceResult> GetRealmPresenceAsync(GetRealmPresenceRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<RealmPresenceResult>(new NotSupportedException("Realm presence is not available."));
    Task SetPresenceEnabledAsync(SetPresenceEnabledRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Presence privacy settings are not available."));
    Task UpdateOwnPresenceAsync(UpdateOwnPresenceRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Presence reporting is not available."));
    Task UpdateOwnUserStatusAsync(UpdateOwnUserStatusRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("User status updates are not available."));
    Task<EventBatch> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default);
    Task<HistoryResult> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default);
    Task<HistoryResult> GetMessagesAroundAsync(MessageAroundRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<HistoryResult>(new NotSupportedException("Loading message context is not available."));
    Task<MessageQueryPage> SearchMessagesAsync(MessageSearchRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Server message search is not available."));
    Task<MessageQueryPage> LoadSavedMessagesAsync(SavedMessagesRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Saved messages are not available."));
    Task<TopicsResult> GetTopicsAsync(TopicsRequest request, CancellationToken cancellationToken = default);
    Task SetTopicVisibilityPolicyAsync(SetTopicVisibilityPolicyRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Topic visibility settings are not available."));
    Task<TopicReadResult> MarkTopicReadAsync(MarkTopicReadRequest request, CancellationToken cancellationToken = default) => Task.FromException<TopicReadResult>(new NotSupportedException("Topic read state is not available."));
    Task<TopicAnchorResult> ResolveTopicAnchorAsync(ResolveTopicAnchorRequest request, CancellationToken cancellationToken = default) => Task.FromException<TopicAnchorResult>(new NotSupportedException("Topic anchors are not available."));
    Task MoveTopicAsync(MoveTopicRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Moving topics is not available."));
    Task<TopicDeleteResult> DeleteTopicAsync(DeleteTopicRequest request, CancellationToken cancellationToken = default) => Task.FromException<TopicDeleteResult>(new NotSupportedException("Deleting topics is not available."));
    Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default);
    Task SetReactionAsync(SetReactionRequest request, CancellationToken cancellationToken = default);
    Task EditMessageAsync(EditMessageRequest request, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default);
    Task SetMessageStarredAsync(SetMessageStarredRequest request, CancellationToken cancellationToken = default);
    Task<UploadedAttachment> UploadAttachmentAsync(UploadAttachmentRequest request, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetRealmMediaAsync(GetRealmMediaRequest request, CancellationToken cancellationToken = default);
    Task<RealmMediaDownloadResult> DownloadRealmMediaAsync(
        GetRealmMediaRequest request,
        Stream destination,
        IProgress<RealmMediaTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<RealmMediaDownloadResult>(new NotSupportedException("Streaming media downloads are not available."));
    Task<UnsubscribeChannelResult> UnsubscribeChannelAsync(UnsubscribeChannelRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(AvailableChannelsRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChannelSummary>>([]);
    Task<SubscribeChannelResult> SubscribeToChannelAsync(SubscribeChannelRequest request, CancellationToken cancellationToken = default) => Task.FromException<SubscribeChannelResult>(new NotSupportedException());
    Task SetSubscriptionPreferenceAsync(SetSubscriptionPreferenceRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task<ChannelSettingsSnapshot> GetChannelSettingsSnapshotAsync(ChannelSettingsSnapshotRequest request, CancellationToken cancellationToken = default) => Task.FromException<ChannelSettingsSnapshot>(new NotSupportedException("Channel settings are not available."));
    Task<ChannelDetails> GetChannelDetailsAsync(ChannelDetailsRequest request, CancellationToken cancellationToken = default) => Task.FromException<ChannelDetails>(new NotSupportedException("Channel settings are not available."));
    Task UpdateChannelAsync(UpdateChannelRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task<ChannelFolder> CreateChannelFolderAsync(CreateChannelFolderRequest request, CancellationToken cancellationToken = default) => Task.FromException<ChannelFolder>(new NotSupportedException("Channel settings are not available."));
    Task<string> GetChannelEmailAddressAsync(ChannelEmailAddressRequest request, CancellationToken cancellationToken = default) => Task.FromException<string>(new NotSupportedException("Channel settings are not available."));
    Task ArchiveChannelAsync(ArchiveChannelRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task<long> CreateChannelAsync(CreateChannelRequest request, CancellationToken cancellationToken = default) => Task.FromException<long>(new NotSupportedException("Channel creation is not available."));
    Task<long> CreatePrivateGroupAsync(PrivateGroupCreateRequest request, CancellationToken cancellationToken = default) => Task.FromException<long>(new NotSupportedException("Private-group creation is not available."));
    Task<ChannelPersonalSettings> GetChannelPersonalSettingsAsync(ChannelMembersRequest request, CancellationToken cancellationToken = default) => Task.FromException<ChannelPersonalSettings>(new NotSupportedException("Channel personal settings are not available."));
    Task SetChannelPersonalSettingAsync(SetChannelPersonalSettingRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel personal settings are not available."));
    Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(RealmUsersRequest request, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<UserProfile>>(new NotSupportedException("Realm users are not available."));
    Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(ChannelMembersRequest request, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<long>>(new NotSupportedException("Channel members are not available."));
    Task ModifyChannelMembersAsync(ModifyChannelMembersRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel members are not available."));
    Task UpdateChannelAdvancedSettingsAsync(UpdateChannelAdvancedRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("Channel settings are not available."));
    Task MarkReadAsync(MarkReadRequest request, CancellationToken cancellationToken = default);
    Task DeleteQueueAsync(DeleteQueueRequest request, CancellationToken cancellationToken = default);
}
