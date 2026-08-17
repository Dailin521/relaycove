using RelayCove.Core;

namespace RelayCove.App.Services;

internal sealed class NativeShellPreviewSession : IClientSession
{
    private const string PreviewVariable = "RELAYCOVE_NATIVE_UI_PREVIEW";
    private const string PreviewSceneVariable = "RELAYCOVE_NATIVE_UI_PREVIEW_SCENE";
    private const string PreviewThemeVariable = "RELAYCOVE_NATIVE_UI_PREVIEW_THEME";
    private const string PreviewWidthVariable = "RELAYCOVE_NATIVE_UI_PREVIEW_WIDTH";
    private const string PreviewHeightVariable = "RELAYCOVE_NATIVE_UI_PREVIEW_HEIGHT";
    private const long CurrentUser = 6;
    private readonly IReadOnlyList<ConversationKey> _recentDirectMessages;
    private ClientState _state;
    private ConversationKey? _selectedConversation;

    public NativeShellPreviewSession()
    {
        var uiDesign = new ChannelTopic(6, "UI 设计讨论");
        var windowsClient = new ChannelTopic(12, "Windows 客户端");
        var productRoadmap = new ChannelTopic(3, "产品路线图");
        var release = new ChannelTopic(1, "版本发布");
        var mayaDirect = new DirectMessage([9]);
        var alexDirect = new DirectMessage([8]);
        var danielDirect = new DirectMessage([11]);
        var sarahDirect = new DirectMessage([7]);
        var self = new DirectMessage([]);
        var today = DateTime.Today;
        var timestamp = AtLocalTime(today, 9, 28);
        var yesterday = today.AddDays(-1);
        var previousSunday = today.AddDays(-DaysSincePreviousSunday(today.DayOfWeek));
        var thumbsUp = new EmojiReactionIdentity("+1", "1f44d", "unicode_emoji");

        _state = new ClientState(
            messages: new Dictionary<long, ChatMessage>
            {
                [101] = new ChatMessage(
                    101,
                    uiDesign,
                    9,
                    "顶部按微信逻辑收敛，只保留置顶、最小化、最大化和关闭。",
                    timestamp,
                    isRead: true,
                    senderDisplayName: "Maya Chen"),
                [102] = new ChatMessage(
                    102,
                    uiDesign,
                    9,
                    "中栏不要再加额外工具区，只要搜索、新建，以及清楚区分的频道和私信列表。",
                    timestamp.AddMinutes(1),
                    isRead: true,
                    senderDisplayName: "Maya Chen",
                    reactions:
                    [
                        new EmojiReaction(thumbsUp, 9, "Maya Chen"),
                        new EmojiReaction(thumbsUp, 8, "Alex Wu"),
                        new EmojiReaction(thumbsUp, 11, "Daniel Okafor")
                    ]),
                [103] = new ChatMessage(
                    103,
                    uiDesign,
                    CurrentUser,
                    "@_**Maya Chen|9** [said](https://preview.invalid/#narrow/near/102):\n```quote\n只要搜索、新建，以及频道和私信列表\n```\n\n可以。私信区直接列出当前工作区可靠获知的成员，频道仍保留话题上下文。",
                    timestamp.AddMinutes(13),
                    isRead: true,
                    senderDisplayName: "林远"),
                [104] = new ChatMessage(
                    104,
                    uiDesign,
                    8,
                    "我会让失败发送保持显式恢复，不做自动重试。\n![relaycove-team-avatars.png](https://preview.invalid/user_uploads/relaycove-team-avatars.png)",
                    timestamp.AddMinutes(18),
                    senderDisplayName: "Alex Wu"),
                [201] = new ChatMessage(201, windowsClient, 8, "类型检查和生产构建已经纳入本地验证。", AtLocalTime(today, 10, 39), true, "Alex Wu"),
                [202] = new ChatMessage(202, windowsClient, CurrentUser, "收到，浏览器测试继续只使用 mock HTTP。", AtLocalTime(today, 10, 42), true, "林远"),
                [301] = new ChatMessage(301, productRoadmap, 7, "Web 是正式产品，MAUI 在交互版本冻结后原生复刻。", AtLocalTime(yesterday, 17, 40), true, "Sarah Li"),
                [401] = new ChatMessage(401, release, 11, "本轮不推送、不发布，也不触发真实消息写入。", AtLocalTime(previousSunday, 18, 22), true, "Daniel Okafor"),
                [501] = new ChatMessage(501, mayaDirect, 9, "我把下一轮检查项整理好了", AtLocalTime(today, 9, 56), true, "Maya Chen"),
                [502] = new ChatMessage(502, alexDirect, 8, "发送状态那块可以开始联调", AtLocalTime(today, 9, 31), senderDisplayName: "Alex Wu"),
                [503] = new ChatMessage(503, danielDirect, 11, "今晚跑一轮 Windows 11 验收", AtLocalTime(yesterday, 18, 10), true, "Daniel Okafor"),
                [504] = new ChatMessage(504, sarahDirect, 7, "范围说明我补到文档里了", AtLocalTime(yesterday, 17, 20), true, "Sarah Li"),
                [505] = new ChatMessage(505, self, CurrentUser, "备忘：逐个审查设置和失败状态", AtLocalTime(previousSunday, 12, 0), true, "林远")
            },
            subscriptions: new Dictionary<long, Subscription>
            {
                [6] = new Subscription(6, "design"),
                [12] = new Subscription(12, "engineering"),
                [3] = new Subscription(3, "product"),
                [1] = new Subscription(1, "release")
            },
            users: new Dictionary<long, UserProfile>
            {
                [CurrentUser] = new UserProfile(CurrentUser, "林远"),
                [9] = new UserProfile(9, "Maya Chen"),
                [8] = new UserProfile(8, "Alex Wu"),
                [11] = new UserProfile(11, "Daniel Okafor"),
                [7] = new UserProfile(7, "Sarah Li")
            },
            topics: new Dictionary<string, TopicSummary>
            {
                [uiDesign.CanonicalKey] = new TopicSummary(6, "UI 设计讨论", 104),
                [windowsClient.CanonicalKey] = new TopicSummary(12, "Windows 客户端", 202),
                [productRoadmap.CanonicalKey] = new TopicSummary(3, "产品路线图", 301),
                [release.CanonicalKey] = new TopicSummary(1, "版本发布", 401)
            },
            unread: new UnreadState(
                new Dictionary<string, int>
                {
                    [windowsClient.CanonicalKey] = 5,
                    [productRoadmap.CanonicalKey] = 2,
                    [alexDirect.CanonicalKey] = 1
                }),
            connection: new ConnectionState(ConnectionStatus.Connected, "native_ui_preview"));
        _selectedConversation = uiDesign;
        _recentDirectMessages = [mayaDirect, alexDirect, danielDirect, sarahDirect, self];
        if (string.Equals(RequestedScene, "dm-cache-switch", StringComparison.Ordinal))
        {
            SeedCacheSwitchConversation(mayaDirect, 9, "Maya Chen", 6000, today, "缓存会话 A");
            SeedCacheSwitchConversation(alexDirect, 8, "Alex Wu", 7000, today, "缓存会话 B");
        }
    }

    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable(PreviewVariable), "1", StringComparison.Ordinal);

    public static string RequestedScene =>
        Environment.GetEnvironmentVariable(PreviewSceneVariable)?.Trim().ToLowerInvariant() ?? "shell";

    public static string RequestedTheme =>
        Environment.GetEnvironmentVariable(PreviewThemeVariable)?.Trim().ToLowerInvariant() ?? "light";

    public static int RequestedWidth => ParsePreviewDimension(
        Environment.GetEnvironmentVariable(PreviewWidthVariable),
        defaultValue: 1440,
        minimum: 480,
        maximum: 3840);

    public static int RequestedHeight => ParsePreviewDimension(
        Environment.GetEnvironmentVariable(PreviewHeightVariable),
        defaultValue: 900,
        minimum: 560,
        maximum: 2160);

    public AccountId? AccountId { get; } = RelayCove.Core.AccountId.Create(
        RealmEndpoint.Parse("https://preview.invalid"),
        CurrentUser);

    public RealmEndpoint? ActiveRealm { get; } = RealmEndpoint.Parse("https://preview.invalid");
    public long? CurrentUserId => CurrentUser;
    public long MaxFileUploadBytes => 10L * 1024 * 1024;

    public ClientState State => _state;
    public ConversationKey? SelectedConversation => _selectedConversation;
    public ConversationHistoryState HistoryState => new(
        _selectedConversation,
        1,
        false,
        true,
        false,
        _state.Messages.Values.Where(message => message.Conversation == _selectedConversation).Select(message => (long?)message.Id).Min(),
        null);
    public IReadOnlyList<ConversationKey> RecentDirectMessages => _recentDirectMessages;
    internal long UnreadDividerAfterMessageId => 102;
    internal string UnreadDividerLabel => "4 条未读消息";
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
        var message = new ChatMessage(id, conversation, CurrentUser, content, DateTimeOffset.Now, true, "林远");
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
            new EmojiReaction(reaction, CurrentUser, "林远"),
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

    public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DomainReducer.Apply(
            _state,
            new SubscriptionRemovedEvent(channelId, Source: DomainEventSource.Local));
        if (SelectedConversation is ChannelTopic selected && selected.ChannelId == channelId)
        {
            _selectedConversation = null;
        }
        Publish();
        return Task.CompletedTask;
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

    private void SeedCacheSwitchConversation(
        ConversationKey conversation,
        long otherUserId,
        string otherUserName,
        long idBase,
        DateTime today,
        string label)
    {
        var messages = new Dictionary<long, ChatMessage>(_state.Messages);
        for (var index = 1; index <= 120; index++)
        {
            var isOwn = index % 2 == 0;
            var senderId = isOwn ? CurrentUser : otherUserId;
            var senderName = isOwn ? "林远" : otherUserName;
            var messageId = idBase + index;
            var content = index % 12 == 0
                ? $"{label} · 第 {index} 条多行消息。\n切换回来必须直接复用本地窗口，不显示加载空白。\n最终一行用于确认高消息容器仍能稳定滚到底部。"
                : $"{label} · 第 {index} 条消息，切换回来应直接复用本地窗口并保持最新消息可见。";
            messages[messageId] = new ChatMessage(
                messageId,
                conversation,
                senderId,
                content,
                AtLocalTime(today, 11, 0).AddMinutes(index),
                isRead: true,
                senderDisplayName: senderName);
        }
        _state = _state with { Messages = messages };
    }

    private static DateTimeOffset AtLocalTime(DateTime date, int hour, int minute)
    {
        var local = DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static int DaysSincePreviousSunday(DayOfWeek dayOfWeek)
    {
        var days = (int)dayOfWeek;
        return days == 0 ? 7 : days;
    }

    internal static int ParsePreviewDimension(string? value, int defaultValue, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : defaultValue;
}
