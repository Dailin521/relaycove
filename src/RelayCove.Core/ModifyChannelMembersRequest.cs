namespace RelayCove.Core;

public sealed record ModifyChannelMembersRequest(CredentialEnvelope Credentials, string ChannelName, IReadOnlyList<long> PrincipalIds, bool Add, bool SendNewSubscriptionMessages)
{
    public override string ToString() => "ModifyChannelMembersRequest { Credentials = [redacted], Channel = [redacted], Principals = [redacted] }";
}
