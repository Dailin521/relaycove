namespace RelayCove.Core;

public sealed record PrivateGroupTransferResult(
    bool OwnershipTransferred,
    bool PreviousOwnerExited,
    string Status);
