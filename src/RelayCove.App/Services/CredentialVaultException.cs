namespace RelayCove.App.Services;

/// <summary>Controlled vault failure that intentionally omits credential material.</summary>
public sealed class CredentialVaultException : Exception
{
    public CredentialVaultException(CredentialVaultFailure failure)
        : base($"Credential vault operation failed ({failure}).")
    {
        Failure = failure;
    }

    public CredentialVaultFailure Failure { get; }
}
