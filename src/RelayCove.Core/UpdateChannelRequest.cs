namespace RelayCove.Core;

public sealed record UpdateChannelRequest(
    CredentialEnvelope Credentials,
    long ChannelId,
    string? Name,
    string? Description,
    long? FolderId,
    bool ClearFolder = false);
