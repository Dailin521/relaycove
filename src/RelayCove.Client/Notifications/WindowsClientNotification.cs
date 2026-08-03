namespace RelayCove.Client.Notifications;

internal sealed record WindowsClientNotification(
    string Title,
    string Body,
    IReadOnlyList<KeyValuePair<string, string>> ActivationArguments,
    string Tag,
    string Group,
    DateTimeOffset Expiration,
    bool ExpiresOnReboot)
{
    public override string ToString() =>
        $"{nameof(WindowsClientNotification)} {{ Title = [REDACTED], " +
        "Body = [REDACTED], ActivationArguments = [REDACTED], " +
        "Tag = [REDACTED], Group = [REDACTED], Expiration = [REDACTED], " +
        $"ExpiresOnReboot = {ExpiresOnReboot} }}";
}
