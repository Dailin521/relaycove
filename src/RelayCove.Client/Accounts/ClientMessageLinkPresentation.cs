namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageLinkPresentation(
    string DisplayText,
    string AbsoluteUri)
{
    public override string ToString() =>
        $"{nameof(ClientMessageLinkPresentation)} {{ DisplayText = [REDACTED], " +
        "AbsoluteUri = [REDACTED] }";
}
