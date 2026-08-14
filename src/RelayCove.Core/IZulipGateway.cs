namespace RelayCove.Core;

public interface IZulipGateway
{
    Task<RealmProbeResult> ProbeRealmAsync(RealmEndpoint realm, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default);
    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<EventBatch> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default);
    Task<HistoryResult> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default);
    Task<HistoryResult> GetMessagesAroundAsync(MessageAroundRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<HistoryResult>(new NotSupportedException("Loading message context is not available."));
    Task<MessageQueryPage> SearchMessagesAsync(MessageSearchRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Server message search is not available."));
    Task<MessageQueryPage> LoadSavedMessagesAsync(SavedMessagesRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<MessageQueryPage>(new NotSupportedException("Saved messages are not available."));
    Task<TopicsResult> GetTopicsAsync(TopicsRequest request, CancellationToken cancellationToken = default);
    Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default);
    Task SetReactionAsync(SetReactionRequest request, CancellationToken cancellationToken = default);
    Task EditMessageAsync(EditMessageRequest request, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default);
    Task SetMessageStarredAsync(SetMessageStarredRequest request, CancellationToken cancellationToken = default);
    Task<UploadedAttachment> UploadAttachmentAsync(UploadAttachmentRequest request, CancellationToken cancellationToken = default);
    Task<RealmMediaResult> GetRealmMediaAsync(GetRealmMediaRequest request, CancellationToken cancellationToken = default);
    Task<UnsubscribeChannelResult> UnsubscribeChannelAsync(UnsubscribeChannelRequest request, CancellationToken cancellationToken = default);
    Task MarkReadAsync(MarkReadRequest request, CancellationToken cancellationToken = default);
    Task DeleteQueueAsync(DeleteQueueRequest request, CancellationToken cancellationToken = default);
}
