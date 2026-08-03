namespace RelayCove.Client.Storage;

public enum LocalCacheOperationStatus
{
    Ready = 1,
    UnknownConversation = 2,
    RevokedConversation = 3,
    FatalScope = 4,
}
