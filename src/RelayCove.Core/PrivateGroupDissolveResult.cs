namespace RelayCove.Core;

public sealed record PrivateGroupDissolveResult(
    bool OtherMembersRemoved,
    bool OwnerExited,
    string Status);
