namespace RelayCove.Shared.Admin;

public sealed record ResetUserPasswordRequest(string Password)
{
    public override string ToString() =>
        $"{nameof(ResetUserPasswordRequest)} {{ Password = [REDACTED] }}";
}
