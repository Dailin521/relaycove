namespace RelayCove.Core;

public sealed record CreateChannelFolderRequest(CredentialEnvelope Credentials, string Name, string? Description);
