namespace RelayCove.Core;

public interface IClientSession
{
    AccountId? AccountId { get; }
    ClientState State { get; }
    ConversationKey? SelectedConversation { get; }
    IReadOnlyList<ConversationKey> RecentDirectMessages { get; }
    event EventHandler<ClientStateChangedEventArgs>? StateChanged;
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);
    Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default);
    Task LoadOlderAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default);
    Task SendAsync(string content, CancellationToken cancellationToken = default);
    Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default);
    Task ClearLocalCacheAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
