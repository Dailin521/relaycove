namespace RelayCove.Client.Accounts;

internal sealed record ClientConversationListItemPresentation(
    Guid Id,
    ClientConversationGroup Group,
    string GroupTitle,
    string TypeIcon,
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
        $"Group = {Group}, GroupTitle = {GroupTitle}, TypeIcon = {TypeIcon}, " +
        "AvatarText = [REDACTED], Name = [REDACTED], " +
        $"TypeLabel = {TypeLabel}, Preview = [REDACTED], " +
        $"Timestamp = {Timestamp}, UnreadText = {UnreadText}, " +
        $"HasUnread = {HasUnread}, MutedLabel = {MutedLabel} }}";
}
