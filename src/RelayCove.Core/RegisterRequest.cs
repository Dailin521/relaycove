namespace RelayCove.Core;

public sealed class RegisterRequest
{
    public RegisterRequest(
        CredentialEnvelope credentials,
        IReadOnlyCollection<string>? eventTypes = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (eventTypes?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException("Event types cannot contain empty values.", nameof(eventTypes));
        }

        Credentials = credentials;
        EventTypes = eventTypes is null
            ? null
            : Array.AsReadOnly(eventTypes.ToArray());
    }

    public CredentialEnvelope Credentials { get; }
    public IReadOnlyCollection<string>? EventTypes { get; }

    public override string ToString() =>
        "RegisterRequest { Credentials = [redacted], EventTypes = [redacted] }";
}
