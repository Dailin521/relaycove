namespace RelayCove.Server.Services;

public enum PasswordVerificationOutcome
{
    Failed = 0,
    Success = 1,
    SuccessRehashNeeded = 2,
}
