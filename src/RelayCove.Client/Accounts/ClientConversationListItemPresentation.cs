namespace RelayCove.Client.Accounts;

internal sealed record ClientConversationListItemPresentation(
    Guid Id,
    string AvatarText,
    string Name,
    string TypeLabel,
    string Preview,
    string Timestamp,
    string UnreadText,
    bool HasUnread,
    string MutedLabel)
{
    public override string ToString() =>
        $"{nameof(ClientConversationListItemPresentation)} {{ Id = {Id}, " +
        "AvatarText = [REDACTED], Name = [REDACTED], " +
        $"TypeLabel = {TypeLabel}, Preview = [REDACTED], " +
        $"Timestamp = {Timestamp}, UnreadText = {UnreadText}, " +
        $"HasUnread = {HasUnread}, MutedLabel = {MutedLabel} }}";
}
