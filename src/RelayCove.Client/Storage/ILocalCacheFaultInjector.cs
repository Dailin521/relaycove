namespace RelayCove.Client.Storage;

internal interface ILocalCacheFaultInjector
{
    void BeforeRevocationTombstone(Guid conversationId);

    void BeforeAuthoritativeSnapshotCommit()
    {
    }

    void BeforeReadPendingReadThroughBatch()
    {
    }
}
