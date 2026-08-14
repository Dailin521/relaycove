using System.Windows.Input;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record MessageItem
{
    public MessageItem(
        string id,
        long? messageId,
        long? senderId,
        string sender,
        string content,
        string timestamp,
        bool isOwn = false,
        bool isUnread = false,
        bool isBot = false,
        string? senderAvatarUrl = null,
        bool isStarred = false,
        IReadOnlyList<ReactionItem>? reactions = null,
        string? permalink = null,
        bool showDateDivider = false,
        string? dateDividerLabel = null,
        bool showUnreadDivider = false,
        string? unreadDividerLabel = null,
        string? mutationState = null,
        bool mutationBlocksActions = false,
        string? deliveryState = null,
        bool canRecover = false,
        ICommand? recoverCommand = null,
        RealmEndpoint? realm = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        ArgumentNullException.ThrowIfNull(content);
        Id = id;
        MessageId = messageId;
        SenderId = senderId;
        Sender = sender;
        Content = content;
        Timestamp = timestamp;
        IsOwn = isOwn;
        IsUnread = isUnread;
        IsBot = isBot;
        SenderAvatarUrl = senderAvatarUrl;
        IsStarred = isStarred;
        Reactions = (reactions ?? []).ToArray();
        Permalink = permalink;
        ShowDateDivider = showDateDivider;
        DateDividerLabel = dateDividerLabel;
        ShowUnreadDivider = showUnreadDivider;
        UnreadDividerLabel = string.IsNullOrWhiteSpace(unreadDividerLabel) ? "未读消息" : unreadDividerLabel;
        MutationState = mutationState;
        MutationBlocksActions = mutationBlocksActions;
        DeliveryState = deliveryState;
        CanRecover = canRecover;
        RecoverCommand = recoverCommand;
        Realm = realm;

        var presentation = MessageContentPresentation.Parse(content, Realm);
        QuoteSender = presentation.QuoteSender;
        QuoteBody = presentation.QuoteBody;
        Body = presentation.Body;
        Attachments = presentation.Attachments;
    }

    public string Id { get; }
    public long? MessageId { get; }
    public long? SenderId { get; }
    public string Sender { get; }
    public string Content { get; }
    public string Body { get; }
    public string Timestamp { get; }
    public bool IsOwn { get; }
    public bool IsOther => !IsOwn;
    public bool IsUnread { get; }
    public bool IsBot { get; }
    public string? SenderAvatarUrl { get; }
    public bool HasSenderAvatar => !string.IsNullOrWhiteSpace(SenderAvatarUrl);
    public bool ShowAvatarFallback => !HasSenderAvatar;
    public bool IsStarred { get; }
    public IReadOnlyList<ReactionItem> Reactions { get; }
    public string? Permalink { get; }
    public bool ShowDateDivider { get; }
    public string? DateDividerLabel { get; }
    public bool ShowUnreadDivider { get; }
    public string UnreadDividerLabel { get; }
    public string? MutationState { get; }
    public bool MutationBlocksActions { get; }
    public string? DeliveryState { get; }
    public bool CanRecover { get; }
    public ICommand? RecoverCommand { get; }
    public RealmEndpoint? Realm { get; }
    public string? QuoteSender { get; }
    public string? QuoteBody { get; }
    public bool HasQuote => QuoteSender is not null;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public IReadOnlyList<MessageAttachmentItem> Attachments { get; private set; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasReactions => Reactions.Count > 0;
    public bool HasMutationState => !string.IsNullOrWhiteSpace(MutationState);
    public bool HasDeliveryState => !string.IsNullOrWhiteSpace(DeliveryState);
    public bool CanMutate => MessageId is not null && !MutationBlocksActions;
    public bool CanEditOrDelete => CanMutate && IsOwn;
    public int AvatarColumn => IsOwn ? 2 : 0;
    public Brush ToneBrush => new SolidColorBrush(
        Color.FromArgb(TonePalette[(int)(Math.Abs((SenderId ?? 0) % TonePalette.Length))]));
    public string AvatarInitial => AvatarInitials.Create(Sender, IsBot);
    public string AccessibleLabel => $"{Sender}，{Timestamp}。{Content}";

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8268A9", "#43846F", "#D69B60", "#D65B78", "#657681"
    ];
}
