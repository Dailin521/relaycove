namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageListItemPresentation(
    long Id,
    string SenderLabel,
    string Content,
    string Timestamp,
    bool IsOwnMessage)
{
    public override string ToString() =>
        $"{nameof(ClientMessageListItemPresentation)} {{ Id = [REDACTED], " +
        "SenderLabel = [REDACTED], Content = [REDACTED], " +
        $"Timestamp = [REDACTED], IsOwnMessage = {IsOwnMessage} }}";
}
