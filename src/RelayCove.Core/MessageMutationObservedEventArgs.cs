namespace RelayCove.Core;

public sealed class MessageMutationObservedEventArgs(
    IReadOnlyCollection<long> messageIds,
    bool deleted,
    bool? isStarred) : EventArgs
{
    public IReadOnlyCollection<long> MessageIds { get; } = messageIds;
    public bool Deleted { get; } = deleted;
    public bool? IsStarred { get; } = isStarred;
}
