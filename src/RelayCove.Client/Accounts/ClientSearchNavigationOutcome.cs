namespace RelayCove.Client.Accounts;

internal sealed record ClientSearchNavigationOutcome(ClientSearchNavigationStatus Status)
{
    public static ClientSearchNavigationOutcome Failure(ClientSearchNavigationStatus status) =>
        new(status);

    public override string ToString() =>
        $"{nameof(ClientSearchNavigationOutcome)} {{ Status = {Status} }}";
}
