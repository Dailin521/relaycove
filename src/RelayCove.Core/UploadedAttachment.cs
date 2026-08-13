namespace RelayCove.Core;

public sealed record UploadedAttachment(string FileName, string Url)
{
    public override string ToString() => "UploadedAttachment { FileName = [redacted], Url = [redacted] }";
}
