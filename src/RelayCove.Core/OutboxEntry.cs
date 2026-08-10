namespace RelayCove.Core;

public sealed record OutboxEntry
{
    public OutboxEntry(
        string localId,
        ConversationKey conversation,
        string content,
        DateTimeOffset createdAt,
        OutboxState state = OutboxState.Hidden,
        OutboxFailureKind? failure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        LocalId = localId;
        Conversation = conversation;
        Content = content;
        CreatedAt = createdAt;
        State = state;
        Failure = failure;
    }

    public string LocalId { get; init; }
    public ConversationKey Conversation { get; init; }
    public string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public OutboxState State { get; init; }
    public OutboxFailureKind? Failure { get; init; }

    public override string ToString() =>
        $"OutboxEntry {{ LocalId = [redacted], Conversation = [redacted], Content = [redacted], CreatedAt = {CreatedAt:O}, State = {State}, Failure = {Failure} }}";
}
