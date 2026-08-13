namespace RelayCove.Core;

public sealed record RealmMediaResult(byte[] Content, string ContentType)
{
    public override string ToString() =>
        $"RealmMediaResult {{ Content = [redacted {Content.LongLength} bytes], ContentType = {ContentType} }}";
}
