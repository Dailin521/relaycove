namespace RelayCove.Shared.Messages;

public enum IncomingMessageMergeResult
{
    Inserted = 1,
    PendingPromoted = 2,
    Duplicate = 3,
    Conflict = 4,
}
