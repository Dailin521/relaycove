namespace RelayCove.Client.Storage;

internal interface ILocalCacheFaultInjector
{
    void BeforeRevocationTombstone(Guid conversationId);

    void BeforeSchemaCommit()
    {
    }

    void BeforeAuthoritativeSnapshotCommit()
    {
    }

    void BeforeReadPendingReadThroughBatch()
    {
    }

    void BeforeNotificationAdoptionCommit()
    {
    }

    void BeforeNotificationHandledCommit()
    {
    }
}
