using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

internal static class ClientAttachmentMetadataPolicy
{
    internal const int MaximumAttachmentsPerMessage = 10;
    internal const int MaximumOriginalFileNameScalars = 255;
    internal const int MaximumContentTypeLength = 127;
    internal const long AbsoluteMaximumAttachmentSize = 100L * 1024 * 1024;

    public static bool IsValidCollection(MessageType messageType, IReadOnlyList<AttachmentDto> attachments)
    {
        if (messageType is MessageType.Text or MessageType.System)
        {
            return attachments.Count == 0;
        }

        if (messageType is not MessageType.Image and not MessageType.File ||
            attachments.Count is < 1 or > MaximumAttachmentsPerMessage)
        {
            return false;
        }

        Guid? previousId = null;
        foreach (var attachment in attachments)
        {
            if (attachment is null ||
                !IsValid(attachment) ||
                (messageType == MessageType.Image &&
                 !attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ||
                (previousId.HasValue && previousId.Value.CompareTo(attachment.Id) >= 0))
            {
                return false;
            }

            previousId = attachment.Id;
        }

        return true;
    }

    public static bool IsValid(AttachmentDto attachment)
    {
        if (attachment is null ||
            attachment.Id == Guid.Empty ||
            attachment.Size is < 1 or > AbsoluteMaximumAttachmentSize ||
            attachment.ThumbnailUrl is not null ||
            !string.Equals(
                attachment.DownloadUrl,
                $"/api/attachments/{attachment.Id:D}/download",
                StringComparison.Ordinal) ||
            !IsValidOriginalFileName(attachment.OriginalFileName) ||
            !TryCanonicalizeContentType(
                attachment.ContentType,
                out var canonicalContentType))
        {
            return false;
        }

        return string.Equals(
            attachment.ContentType,
            canonicalContentType,
            StringComparison.Ordinal);
    }

    internal static bool IsValidOriginalFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var scalarCount = 0;
        var remaining = fileName.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done ||
                rune.Value is '/' or '\\' ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > MaximumOriginalFileNameScalars)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    internal static bool TryCanonicalizeContentType(
        string? contentType,
        out string canonicalContentType)
    {
        canonicalContentType = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType) ||
            contentType.Length > MaximumContentTypeLength ||
            contentType.Contains('*', StringComparison.Ordinal) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType) ||
            parsedContentType.MediaType is null)
        {
            return false;
        }

        canonicalContentType = parsedContentType.MediaType.ToLowerInvariant();
        return true;
    }
}
