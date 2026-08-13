namespace RelayCove.Core;

public sealed record SetMessageStarredRequest(CredentialEnvelope Credentials, long MessageId, bool IsStarred);
