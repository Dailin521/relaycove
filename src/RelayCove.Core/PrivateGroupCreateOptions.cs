namespace RelayCove.Core;

public sealed record PrivateGroupCreateOptions(
    string Name,
    IReadOnlyList<long> OtherMemberIds);
