namespace RelayCove.Core;

public enum OutboxState
{
    Hidden,
    Waiting,
    WaitExpired,
    Failed
}
