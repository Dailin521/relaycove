using System.Buffers;
using System.Globalization;
using System.Text;

namespace RelayCove.Server.Data.Entities;

public sealed class Attachment
{
    public const int MaximumOriginalFileNameLength = 255;
    public const int MaximumContentTypeLength = 127;
    public const int StoredFileNameLength = 65;
    public const int Sha256Length = 64;

    private Attachment()
    {
    }

    public Attachment(
        Guid id,
        Guid uploaderUserId,
        string originalFileName,
        string storedFileName,
        string contentType,
        long size,
        string sha256,
        DateTime createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Attachment IDs cannot be empty.", nameof(id));
        }

        if (uploaderUserId == Guid.Empty)
        {
            throw new ArgumentException("Uploader IDs cannot be empty.", nameof(uploaderUserId));
        }

        ValidateOriginalFileName(originalFileName);
        if (storedFileName.Length != StoredFileNameLength ||
            !storedFileName.StartsWith(id.ToString("N"), StringComparison.Ordinal) ||
            storedFileName[32] != '_' ||
            storedFileName[(storedFileName.IndexOf('_') + 1)..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Stored file names must use the managed format.", nameof(storedFileName));
        }

        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > MaximumContentTypeLength)
        {
            throw new ArgumentException("Content types must be bounded and non-empty.", nameof(contentType));
        }

        if (size is < 1 or > Options.UploadOptions.AbsoluteMaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Attachment sizes are outside the supported range.");
        }

        if (sha256.Length != Sha256Length || sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 values must use lowercase hexadecimal.", nameof(sha256));
        }

        Id = id;
        UploaderUserId = uploaderUserId;
        OriginalFileName = originalFileName;
        StoredFileName = storedFileName;
        ContentType = contentType;
        Size = size;
        Sha256 = sha256;
        CreatedAt = SqliteValueConverters.NormalizeUtc(createdAt, nameof(createdAt));
    }

    public Guid Id { get; private set; }

    public long? MessageId { get; private set; }

    public Guid UploaderUserId { get; private set; }

    public string OriginalFileName { get; private set; } = string.Empty;

    public string StoredFileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public Message? Message { get; private set; }

    public User UploaderUser { get; private set; } = null!;

    private static void ValidateOriginalFileName(string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName) ||
            originalFileName is "." or ".." ||
            !string.Equals(originalFileName, originalFileName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Original file names must be non-empty display names.", nameof(originalFileName));
        }

        var scalarCount = 0;
        var remaining = originalFileName.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done ||
                rune.Value is '/' or '\\' ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                throw new ArgumentException("Original file names contain unsupported characters.", nameof(originalFileName));
            }

            scalarCount++;
            if (scalarCount > MaximumOriginalFileNameLength)
            {
                throw new ArgumentOutOfRangeException(nameof(originalFileName), "Original file names are too long.");
            }

            remaining = remaining[consumed..];
        }
    }
}
