#if DEBUG
using RelayCove.Core;

namespace RelayCove.App.Services;

internal sealed class NativeShellPreviewSession : IClientSession
{
    private const string PreviewVariable = "RELAYCOVE_NATIVE_UI_PREVIEW";
    private readonly IReadOnlyList<ConversationKey> _recentDirectMessages;
    private ClientState _state;
    private ConversationKey? _selectedConversation;

    public NativeShellPreviewSession()
    {
        var release = new ChannelTopic(4, "release");
        var design = new ChannelTopic(5, "native-ui");
        var direct = new DirectMessage([8]);
        var group = new DirectMessage([8, 9]);
        var self = new DirectMessage([]);
        var timestamp = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(8));
        var thumbsUp = new EmojiReactionIdentity("+1", "1f44d", "unicode_emoji");
        var celebration = new EmojiReactionIdentity("tada", "1f389", "unicode_emoji");

        _state = new ClientState(
            messages: new Dictionary<long, ChatMessage>
            {
                [101] = new ChatMessage(
                    101,
                    release,
                    7,
                    "Stage 22M 使用纯原生 MAUI 组件，并直接投影 IClientSession 状态。",
                    timestamp,
                    isRead: true,
                    senderDisplayName: "Ada",
                    senderAvatarUrl: "/user_avatars/7/avatar.png",
                    isStarred: true,
                    reactions:
                    [
                        new EmojiReaction(thumbsUp, 7, "Ada"),
                        new EmojiReaction(thumbsUp, 8, "Bea"),
                        new EmojiReaction(celebration, 9, "Chen")
                    ]),
                [102] = new ChatMessage(
                    102,
                    release,
                    8,
                    "@_**Ada|7** [said](https://preview.invalid/#narrow/near/101):\n```quote\nStage 22M 使用纯原生 MAUI 组件，并直接投影 IClientSession 状态。\n```\n\n当前预览完全离线，不连接真实 Realm。",
                    timestamp.AddMinutes(4),
                    senderDisplayName: "Bea",
                    senderAvatarUrl: "/user_avatars/8/avatar.png"),
                [105] = new ChatMessage(
                    105,
                    release,
                    9,
                    "图片与附件由受控同 Realm 媒体层读取。\n![native-shell.png](https://preview.invalid/user_uploads/preview/native-shell.png)\n[interaction-spec.pdf](https://preview.invalid/user_uploads/preview/interaction-spec.pdf)",
                    timestamp.AddMinutes(6),
                    senderDisplayName: "Chen",
                    senderAvatarUrl: "/user_avatars/9/avatar.png"),
                [103] = new ChatMessage(103, direct, 8, "1024 × 768 下详情默认收起。", timestamp.AddMinutes(8), senderDisplayName: "Bea"),
                [104] = new ChatMessage(104, design, 9, "浅色、深色与键盘焦点使用同一组原生 Token。", timestamp.AddMinutes(12), senderDisplayName: "Chen")
            },
            subscriptions: new Dictionary<long, Subscription>
            {
                [4] = new Subscription(4, "engineering"),
                [5] = new Subscription(5, "product-design")
            },
            users: new Dictionary<long, UserProfile>
            {
                [7] = new UserProfile(7, "Ada", avatarUrl: "/user_avatars/7/avatar.png"),
                [8] = new UserProfile(8, "Bea", avatarUrl: "/user_avatars/8/avatar.png"),
                [9] = new UserProfile(9, "Chen", avatarUrl: "/user_avatars/9/avatar.png", isBot: true)
            },
            topics: new Dictionary<string, TopicSummary>
            {
                [release.CanonicalKey] = new TopicSummary(4, "release", 105),
                [design.CanonicalKey] = new TopicSummary(5, "native-ui", 104)
            },
            outbox: new Dictionary<string, OutboxEntry>
            {
                ["1"] = new OutboxEntry("1", release, "等待服务器事件的只读状态示例", timestamp.AddMinutes(16), OutboxState.Waiting)
            },
            unread: new UnreadState(
                new Dictionary<string, int>
                {
                    [release.CanonicalKey] = 2,
                    [direct.CanonicalKey] = 1
                }),
            connection: new ConnectionState(ConnectionStatus.Connected, "native_ui_preview"));
        _selectedConversation = release;
        _recentDirectMessages = [direct, group, self];
    }

    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable(PreviewVariable), "1", StringComparison.Ordinal);

    public AccountId? AccountId { get; } = RelayCove.Core.AccountId.Create(
        RealmEndpoint.Parse("https://preview.invalid"),
        7);

    public RealmEndpoint? ActiveRealm { get; } = RealmEndpoint.Parse("https://preview.invalid");
    public long? CurrentUserId => 7;
    public long MaxFileUploadBytes => 10L * 1024 * 1024;

    public ClientState State => _state;
    public ConversationKey? SelectedConversation => _selectedConversation;
    public IReadOnlyList<ConversationKey> RecentDirectMessages => _recentDirectMessages;
    public event EventHandler<ClientStateChangedEventArgs>? StateChanged;

    public Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish();
        return Task.FromResult(true);
    }

    public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("The native UI preview is read-only."));

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("The native UI preview is read-only."));

    public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _selectedConversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Publish();
        return Task.CompletedTask;
    }

    public Task LoadOlderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TopicSummary> topics = _state.Topics.Values
            .Where(topic => topic.ChannelId == channelId)
            .OrderByDescending(topic => topic.MaxMessageId)
            .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(topics);
    }

    public Task SendAsync(string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var conversation = _selectedConversation ?? throw new InvalidOperationException("No conversation is selected.");
        var id = _state.Messages.Keys.DefaultIfEmpty(100).Max() + 1;
        var message = new ChatMessage(id, conversation, 7, content, DateTimeOffset.Now, true, "Ada");
        _state = DomainReducer.Apply(_state, new MessageUpsertEvent(message, Source: DomainEventSource.Local));
        Publish();
        return Task.CompletedTask;
    }

    public Task SetReactionAsync(
        long messageId,
        EmojiReactionIdentity reaction,
        bool add,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DomainReducer.Apply(_state, new MessageReactionChangedEvent(
            messageId,
            new EmojiReaction(reaction, 7, "Ada"),
            add,
            Source: DomainEventSource.Local));
        Publish();
        return Task.CompletedTask;
    }

    public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwnMessage(messageId);
        _state = DomainReducer.Apply(_state, new MessageContentChangedEvent(messageId, content, Source: DomainEventSource.Local));
        Publish();
        return Task.CompletedTask;
    }

    public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwnMessage(messageId);
        _state = DomainReducer.Apply(_state, new MessageDeletedEvent([messageId], Source: DomainEventSource.Local));
        Publish();
        return Task.CompletedTask;
    }

    public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DomainReducer.Apply(_state, new MessageFlagsChangedEvent(
            [messageId],
            false,
            isStarred ? MessageFlagOperation.Add : MessageFlagOperation.Remove,
            "starred",
            Source: DomainEventSource.Local));
        Publish();
        return Task.CompletedTask;
    }

    public Task<UploadedAttachment> UploadAttachmentAsync(
        AttachmentUpload upload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UploadedAttachment(
            upload.FileName,
            $"https://preview.invalid/user_uploads/preview/{Uri.EscapeDataString(upload.FileName)}"));
    }

    public Task<RealmMediaResult> GetRealmMediaAsync(
        RealmMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pixel = Convert.FromBase64String(
            "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");
        return Task.FromResult(new RealmMediaResult(pixel, "image/png"));
    }

    public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("The native UI preview cannot change read state."));

    public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("The native UI preview has no cache to clear."));

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private void RequireOwnMessage(long messageId)
    {
        if (!_state.Messages.TryGetValue(messageId, out var message) || message.SenderId != CurrentUserId)
        {
            throw new InvalidOperationException("Only preview messages owned by the current user can be changed.");
        }
    }

    private void Publish() => StateChanged?.Invoke(this, new ClientStateChangedEventArgs(_state));
}
#endif
