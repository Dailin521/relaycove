using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;

namespace RelayCove.Server.Services;

public sealed class AttachmentMultipartReader(
    AttachmentStoragePaths storagePaths,
    UploadSettingsService uploadSettingsService,
    ILogger<AttachmentMultipartReader> logger)
{
    private const string FileFieldName = "file";
    private const string DefaultContentType = "application/octet-stream";
    private const int MaximumBoundaryLength = 128;
    private const int MaximumHeaderCount = 8;
    private const int MaximumHeaderLength = 8 * 1024;
    private const int CopyBufferLength = 64 * 1024;

    public async Task<AttachmentUploadReadResult> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Read the persisted setting exactly once before consuming the request body so
        // an administrator update cannot make one upload observe two limits.
        var maximumFileBytes = await uploadSettingsService
            .GetEffectiveMaximumFileBytesAsync(cancellationToken);
        var maximumRequestBytes = checked(maximumFileBytes + UploadOptions.MultipartOverheadBytes);
        if (request.ContentLength is long contentLength && contentLength > maximumRequestBytes)
        {
            return AttachmentUploadReadResult.TooLarge();
        }

        var requestSizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSizeFeature is { IsReadOnly: false })
        {
            requestSizeFeature.MaxRequestBodySize = maximumRequestBytes;
        }

        if (!TryGetBoundary(request.ContentType, out var boundary))
        {
            return await DrainStreamAsync(request.Body, maximumRequestBytes, cancellationToken)
                ? AttachmentUploadReadResult.InvalidRequest()
                : AttachmentUploadReadResult.TooLarge();
        }

        AttachmentStagedUpload? stagedUpload = null;
        try
        {
            var reader = new MultipartReader(boundary, request.Body)
            {
                BodyLengthLimit = checked(maximumFileBytes + 1),
                HeadersCountLimit = MaximumHeaderCount,
                HeadersLengthLimit = MaximumHeaderLength,
            };
            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null ||
                !TryReadMetadata(section, out var originalFileName, out var contentType))
            {
                return await DrainMultipartAsync(reader, section, maximumFileBytes, cancellationToken)
                    ? AttachmentUploadReadResult.InvalidRequest()
                    : AttachmentUploadReadResult.TooLarge();
            }

            stagedUpload = storagePaths.CreateStagedUpload(originalFileName, contentType, logger);
            var copyResult = await CopyToStagingAsync(
                section.Body,
                stagedUpload.StagingPath,
                maximumFileBytes,
                cancellationToken);
            if (copyResult is null)
            {
                await stagedUpload.DisposeAsync();
                stagedUpload = null;
                return AttachmentUploadReadResult.TooLarge();
            }

            if (copyResult.Value.Size == 0)
            {
                await stagedUpload.DisposeAsync();
                stagedUpload = null;
                return AttachmentUploadReadResult.InvalidRequest();
            }

            var extraSection = await reader.ReadNextSectionAsync(cancellationToken);
            if (extraSection is not null)
            {
                var drained = await DrainMultipartAsync(
                    reader,
                    extraSection,
                    maximumFileBytes,
                    cancellationToken);
                await stagedUpload.DisposeAsync();
                stagedUpload = null;
                return drained
                    ? AttachmentUploadReadResult.InvalidRequest()
                    : AttachmentUploadReadResult.TooLarge();
            }

            stagedUpload.Complete(copyResult.Value.Size, copyResult.Value.Sha256);
            return AttachmentUploadReadResult.Success(stagedUpload);
        }
        catch (InvalidDataException)
        {
            if (stagedUpload is not null)
            {
                await stagedUpload.DisposeAsync();
            }

            return AttachmentUploadReadResult.InvalidRequest();
        }
        catch
        {
            if (stagedUpload is not null)
            {
                await stagedUpload.DisposeAsync();
            }

            throw;
        }
    }

    private static bool TryGetBoundary(string? rawContentType, out string boundary)
    {
        boundary = string.Empty;
        if (!MediaTypeHeaderValue.TryParse(rawContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value ?? string.Empty;
        return boundary.Length is > 0 and <= MaximumBoundaryLength &&
               !boundary.Any(character => char.IsControl(character));
    }

    private static bool TryReadMetadata(
        MultipartSection section,
        out string originalFileName,
        out string contentType)
    {
        originalFileName = string.Empty;
        contentType = string.Empty;
        var disposition = section.GetContentDispositionHeader();
        if (disposition is null || !disposition.IsFileDisposition())
        {
            return false;
        }

        var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
        if (!string.Equals(fieldName, FileFieldName, StringComparison.Ordinal))
        {
            return false;
        }

        var rawFileName = disposition.FileNameStar.HasValue
            ? HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
            : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
        if (!TryValidateOriginalFileName(rawFileName, out originalFileName) ||
            !TryNormalizeContentType(section.ContentType, out contentType))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateOriginalFileName(string? value, out string fileName)
    {
        fileName = value ?? string.Empty;
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
            if (scalarCount > Attachment.MaximumOriginalFileNameLength)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool TryNormalizeContentType(string? value, out string contentType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            contentType = DefaultContentType;
            return true;
        }

        if (!MediaTypeHeaderValue.TryParse(value, out var parsed) ||
            !parsed.MediaType.HasValue)
        {
            contentType = string.Empty;
            return false;
        }

        contentType = parsed.MediaType.Value!.ToLowerInvariant();
        return contentType.Length <= Attachment.MaximumContentTypeLength &&
               !contentType.Contains('*', StringComparison.Ordinal);
    }

    private static async Task<(long Size, string Sha256)?> CopyToStagingAsync(
        Stream source,
        string stagingPath,
        long maximumFileBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferLength);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var destination = new FileStream(stagingPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                BufferSize = CopyBufferLength,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
            });
            long size = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                size = checked(size + bytesRead);
                if (size > maximumFileBytes)
                {
                    return null;
                }

                hash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return (size, sha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<bool> DrainMultipartAsync(
        MultipartReader reader,
        MultipartSection? section,
        long maximumSectionBytes,
        CancellationToken cancellationToken)
    {
        while (section is not null)
        {
            if (!await DrainStreamAsync(section.Body, maximumSectionBytes, cancellationToken))
            {
                return false;
            }

            section = await reader.ReadNextSectionAsync(cancellationToken);
        }

        return true;
    }

    private static async Task<bool> DrainStreamAsync(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferLength);
        try
        {
            long totalBytes = 0;
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    return true;
                }

                totalBytes = checked(totalBytes + bytesRead);
                if (totalBytes > maximumBytes)
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
