namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationActivationTarget
{
    private ClientNotificationActivationTarget(
        ClientNotificationActivationKind kind,
        string accountScopeId,
        Guid? conversationId,
        long? messageId)
    {
        WindowsNotificationIdentity.ValidateAccountScopeId(accountScopeId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        AccountScopeId = accountScopeId;
        ConversationId = conversationId;
        MessageId = messageId;
    }

    public ClientNotificationActivationKind Kind { get; }

    public string AccountScopeId { get; }

    public Guid? ConversationId { get; }

    public long? MessageId { get; }

    public static ClientNotificationActivationTarget Message(
        string accountScopeId,
        Guid conversationId,
        long messageId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A notification conversation ID cannot be empty.",
                nameof(conversationId));
        }

        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

        return new ClientNotificationActivationTarget(
            ClientNotificationActivationKind.Message,
            accountScopeId,
            conversationId,
            messageId);
    }

    public static ClientNotificationActivationTarget UnreadOverview(string accountScopeId) =>
        new(
            ClientNotificationActivationKind.UnreadOverview,
            accountScopeId,
            conversationId: null,
            messageId: null);

    public override string ToString() =>
        $"{nameof(ClientNotificationActivationTarget)} {{ Kind = {Kind}, " +
        "AccountScopeId = [REDACTED], ConversationId = [REDACTED], " +
        "MessageId = [REDACTED] }";
}
