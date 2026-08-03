namespace RelayCove.Client.Accounts;

internal enum ClientAccountShellPhase
{
    SignedOut = 0,
    Restoring = 1,
    SigningIn = 2,
    Starting = 3,
    Active = 4,
    Retrying = 5,
    SigningOut = 6,
    Stopping = 7,
}
