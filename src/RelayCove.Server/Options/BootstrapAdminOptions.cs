namespace RelayCove.Server.Options;

public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";

    public bool Enabled { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"{nameof(BootstrapAdminOptions)} {{ Enabled = {Enabled}, UserName = {UserName}, DisplayName = {DisplayName}, Password = [REDACTED] }}";
    }
}
