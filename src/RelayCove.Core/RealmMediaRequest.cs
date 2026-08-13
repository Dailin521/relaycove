namespace RelayCove.Core;

public sealed record RealmMediaRequest(string SourceUrl, RealmMediaKind Kind, long MaximumBytes)
{
    public override string ToString() =>
        $"RealmMediaRequest {{ SourceUrl = [redacted], Kind = {Kind}, MaximumBytes = {MaximumBytes} }}";
}
