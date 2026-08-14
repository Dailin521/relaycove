namespace RelayCove.Core;

public sealed record UnsubscribeChannelResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> NotRemoved);
