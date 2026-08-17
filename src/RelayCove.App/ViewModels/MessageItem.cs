using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed class MessageItem : ObservableObject
{
    private readonly string _id;
    private long? _messageId;
    private long? _senderId;
    private string _sender;
    private string _content;
    private string _body;
    private string _timestamp;
    private bool _isOwn;
    private bool _isUnread;
    private bool _isBot;
    private string? _senderAvatarUrl;
    private bool _isStarred;
    private IReadOnlyList<ReactionItem> _reactions;
    private string? _permalink;
    private bool _showDateDivider;
    private string? _dateDividerLabel;
    private bool _showUnreadDivider;
    private string _unreadDividerLabel;
    private string? _mutationState;
    private bool _mutationBlocksActions;
    private string? _deliveryState;
    private bool _canRecover;
    private ICommand? _recoverCommand;
    private RealmEndpoint? _realm;
    private string? _quoteSender;
    private string? _quoteBody;
    private IReadOnlyList<MessageAttachmentItem> _attachments;

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
        _id = id;
        _messageId = messageId;
        _senderId = senderId;
        _sender = sender;
        _content = content;
        _timestamp = timestamp;
        _isOwn = isOwn;
        _isUnread = isUnread;
        _isBot = isBot;
        _senderAvatarUrl = senderAvatarUrl;
        _isStarred = isStarred;
        _reactions = (reactions ?? []).ToArray();
        _permalink = permalink;
        _showDateDivider = showDateDivider;
        _dateDividerLabel = dateDividerLabel;
        _showUnreadDivider = showUnreadDivider;
        _unreadDividerLabel = string.IsNullOrWhiteSpace(unreadDividerLabel) ? "未读消息" : unreadDividerLabel;
        _mutationState = mutationState;
        _mutationBlocksActions = mutationBlocksActions;
        _deliveryState = deliveryState;
        _canRecover = canRecover;
        _recoverCommand = recoverCommand;
        _realm = realm;

        var presentation = MessageContentPresentation.Parse(content, Realm);
        _quoteSender = presentation.QuoteSender;
        _quoteBody = presentation.QuoteBody;
        _body = presentation.Body;
        _attachments = presentation.Attachments;
    }

    public string Id => _id;
    public long? MessageId => _messageId;
    public long? SenderId => _senderId;
    public string Sender => _sender;
    public string Content => _content;
    public string Body => _body;
    public string Timestamp => _timestamp;
    public bool IsOwn => _isOwn;
    public bool IsOther => !IsOwn;
    public bool IsUnread => _isUnread;
    public bool IsBot => _isBot;
    public string? SenderAvatarUrl => _senderAvatarUrl;
    public bool HasSenderAvatar => !string.IsNullOrWhiteSpace(SenderAvatarUrl);
    public bool ShowAvatarFallback => !HasSenderAvatar;
    public bool IsStarred => _isStarred;
    public IReadOnlyList<ReactionItem> Reactions => _reactions;
    public string? Permalink => _permalink;
    public bool ShowDateDivider => _showDateDivider;
    public string? DateDividerLabel => _dateDividerLabel;
    public bool ShowUnreadDivider => _showUnreadDivider;
    public string UnreadDividerLabel => _unreadDividerLabel;
    public string? MutationState => _mutationState;
    public bool MutationBlocksActions => _mutationBlocksActions;
    public string? DeliveryState => _deliveryState;
    public bool CanRecover => _canRecover;
    public ICommand? RecoverCommand => _recoverCommand;
    public RealmEndpoint? Realm => _realm;
    public string? QuoteSender => _quoteSender;
    public string? QuoteBody => _quoteBody;
    public bool HasQuote => QuoteSender is not null;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public IReadOnlyList<MessageAttachmentItem> Attachments => _attachments;
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

    internal void ApplyFrom(MessageItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(Id, candidate.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Message items with different keys cannot be merged.");
        }

        var identityChanged = SetProperty(ref _messageId, candidate.MessageId, nameof(MessageId));
        if (identityChanged)
        {
            OnPropertyChanged(nameof(CanMutate));
            OnPropertyChanged(nameof(CanEditOrDelete));
        }
        if (SetProperty(ref _senderId, candidate.SenderId, nameof(SenderId)))
        {
            OnPropertyChanged(nameof(ToneBrush));
        }
        if (SetProperty(ref _sender, candidate.Sender, nameof(Sender)))
        {
            OnPropertyChanged(nameof(AvatarInitial));
            OnPropertyChanged(nameof(AccessibleLabel));
        }
        if (SetProperty(ref _timestamp, candidate.Timestamp, nameof(Timestamp)))
        {
            OnPropertyChanged(nameof(AccessibleLabel));
        }
        if (SetProperty(ref _isOwn, candidate.IsOwn, nameof(IsOwn)))
        {
            OnPropertyChanged(nameof(IsOther));
            OnPropertyChanged(nameof(CanEditOrDelete));
            OnPropertyChanged(nameof(AvatarColumn));
        }
        SetProperty(ref _isUnread, candidate.IsUnread, nameof(IsUnread));
        if (SetProperty(ref _isBot, candidate.IsBot, nameof(IsBot)))
        {
            OnPropertyChanged(nameof(AvatarInitial));
        }
        if (SetProperty(ref _senderAvatarUrl, candidate.SenderAvatarUrl, nameof(SenderAvatarUrl)))
        {
            OnPropertyChanged(nameof(HasSenderAvatar));
            OnPropertyChanged(nameof(ShowAvatarFallback));
        }
        SetProperty(ref _isStarred, candidate.IsStarred, nameof(IsStarred));
        if (!_reactions.SequenceEqual(candidate.Reactions))
        {
            _reactions = candidate.Reactions.ToArray();
            OnPropertyChanged(nameof(Reactions));
            OnPropertyChanged(nameof(HasReactions));
        }
        SetProperty(ref _permalink, candidate.Permalink, nameof(Permalink));
        SetProperty(ref _showDateDivider, candidate.ShowDateDivider, nameof(ShowDateDivider));
        SetProperty(ref _dateDividerLabel, candidate.DateDividerLabel, nameof(DateDividerLabel));
        SetProperty(ref _showUnreadDivider, candidate.ShowUnreadDivider, nameof(ShowUnreadDivider));
        SetProperty(ref _unreadDividerLabel, candidate.UnreadDividerLabel, nameof(UnreadDividerLabel));
        if (SetProperty(ref _mutationState, candidate.MutationState, nameof(MutationState)))
        {
            OnPropertyChanged(nameof(HasMutationState));
        }
        if (SetProperty(ref _mutationBlocksActions, candidate.MutationBlocksActions, nameof(MutationBlocksActions)))
        {
            OnPropertyChanged(nameof(CanMutate));
            OnPropertyChanged(nameof(CanEditOrDelete));
        }
        if (SetProperty(ref _deliveryState, candidate.DeliveryState, nameof(DeliveryState)))
        {
            OnPropertyChanged(nameof(HasDeliveryState));
        }
        SetProperty(ref _canRecover, candidate.CanRecover, nameof(CanRecover));
        SetProperty(ref _recoverCommand, candidate.RecoverCommand, nameof(RecoverCommand));

        var presentationChanged = !string.Equals(_content, candidate.Content, StringComparison.Ordinal) ||
            !Equals(_realm, candidate.Realm);
        if (SetProperty(ref _content, candidate.Content, nameof(Content)))
        {
            OnPropertyChanged(nameof(AccessibleLabel));
        }
        SetProperty(ref _realm, candidate.Realm, nameof(Realm));
        if (presentationChanged)
        {
            SetProperty(ref _body, candidate.Body, nameof(Body));
            SetProperty(ref _quoteSender, candidate.QuoteSender, nameof(QuoteSender));
            SetProperty(ref _quoteBody, candidate.QuoteBody, nameof(QuoteBody));
            _attachments = candidate.Attachments.ToArray();
            OnPropertyChanged(nameof(Attachments));
            OnPropertyChanged(nameof(HasBody));
            OnPropertyChanged(nameof(HasQuote));
            OnPropertyChanged(nameof(HasAttachments));
        }
    }

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8268A9", "#43846F", "#D69B60", "#D65B78", "#657681"
    ];
}
