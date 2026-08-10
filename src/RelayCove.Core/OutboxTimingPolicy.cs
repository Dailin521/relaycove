namespace RelayCove.Core;

public static class OutboxTimingPolicy
{
    public const int WaitGraceMilliseconds = 500;
    public const int ExpiryMilliseconds = 10_000;
    public static readonly TimeSpan WaitDuration = TimeSpan.FromMilliseconds(WaitGraceMilliseconds);
    public static readonly TimeSpan ExpiryDuration = TimeSpan.FromMilliseconds(ExpiryMilliseconds);

    public static OutboxEntry Advance(OutboxEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var elapsed = now - entry.CreatedAt;
        return entry.State switch
        {
            OutboxState.Hidden when elapsed >= ExpiryDuration => entry with { State = OutboxState.WaitExpired },
            OutboxState.Hidden when elapsed >= WaitDuration => entry with { State = OutboxState.Waiting },
            OutboxState.Waiting when elapsed >= ExpiryDuration => entry with { State = OutboxState.WaitExpired },
            _ => entry
        };
    }

    public static OutboxEntry MarkFailed(OutboxEntry entry, OutboxFailureKind failure)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with { State = OutboxState.Failed, Failure = failure };
    }
}
