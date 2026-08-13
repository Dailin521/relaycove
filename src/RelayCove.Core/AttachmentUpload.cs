namespace RelayCove.Core;

public sealed class AttachmentUpload
{
    public AttachmentUpload(string fileName, string? contentType, long length, Stream content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        FileName = fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        Length = length;
        Content = content;
    }

    public string FileName { get; }
    public string? ContentType { get; }
    public long Length { get; }
    public Stream Content { get; }

    public override string ToString() =>
        $"AttachmentUpload {{ FileName = [redacted], ContentType = {ContentType ?? "unknown"}, Length = {Length}, Content = [stream] }}";
}
