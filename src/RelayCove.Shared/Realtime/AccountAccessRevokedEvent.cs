namespace RelayCove.Shared.Realtime;

public sealed record AccountAccessRevokedEvent(long MinimumAccessTokenVersion)
{
    public override string ToString() =>
        $"{nameof(AccountAccessRevokedEvent)} {{ MinimumAccessTokenVersion = {MinimumAccessTokenVersion} }}";
}
