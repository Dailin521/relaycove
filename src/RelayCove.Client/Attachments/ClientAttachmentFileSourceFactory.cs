using System.Globalization;
using System.IO;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentFileSourceFactory
{
    private const int StreamBufferSize = 81920;
    private static readonly IReadOnlyDictionary<string, MimeClassification> MimeClassifications =
        new Dictionary<string, MimeClassification>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = new("image/jpeg", true),
            [".jpeg"] = new("image/jpeg", true),
            [".jpe"] = new("image/jpeg", true),
            [".jfif"] = new("image/jpeg", true),
            [".png"] = new("image/png", true),
            [".gif"] = new("image/gif", true),
            [".bmp"] = new("image/bmp", true),
            [".webp"] = new("image/webp", true),
            [".tif"] = new("image/tiff", true),
            [".tiff"] = new("image/tiff", true),
            [".avif"] = new("image/avif", true),
            [".heic"] = new("image/heic", true),
            [".heif"] = new("image/heif", true),
            [".svg"] = new("image/svg+xml", false),
            [".mp4"] = new("video/mp4", false),
            [".m4v"] = new("video/mp4", false),
            [".mov"] = new("video/quicktime", false),
            [".webm"] = new("video/webm", false),
            [".avi"] = new("video/x-msvideo", false),
            [".mkv"] = new("video/x-matroska", false),
            [".pdf"] = new("application/pdf", false),
            [".txt"] = new("text/plain", false),
            [".json"] = new("application/json", false),
            [".zip"] = new("application/zip", false),
        };

    public static async Task<ClientAttachmentFileSelectionOutcome> CreateAsync(
        IReadOnlyList<string>? paths,
        IReadOnlyList<ClientAttachmentFileSelection>? existingSelections = null,
        CancellationToken cancellationToken = default)
    {
        if (paths is null)
        {
            return ClientAttachmentFileSelectionOutcome.Failure(
                ClientAttachmentFileSelectionStatus.NoFilesSelected);
        }

        string[] pathSnapshot;
        ClientAttachmentFileSelection[] existingSnapshot;
        try
        {
            pathSnapshot = paths.ToArray();
            existingSnapshot = existingSelections?.ToArray() ?? [];
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return ClientAttachmentFileSelectionOutcome.Failure(
                ClientAttachmentFileSelectionStatus.InvalidPath);
        }

        if (pathSnapshot.Length == 0)
        {
            return ClientAttachmentFileSelectionOutcome.Failure(
                ClientAttachmentFileSelectionStatus.NoFilesSelected);
        }

        if (existingSnapshot.Any(static selection => selection is null) ||
            existingSnapshot.Length > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage ||
            pathSnapshot.Length > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage -
                existingSnapshot.Length)
        {
            return ClientAttachmentFileSelectionOutcome.Failure(
                ClientAttachmentFileSelectionStatus.TooManyFiles);
        }

        try
        {
            return await Task.Run(
                    () => CreateCore(pathSnapshot, existingSnapshot, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientAttachmentFileSelectionOutcome.Failure(
                ClientAttachmentFileSelectionStatus.Canceled);
        }
    }

    public static MessageType ResolveMessageType(
        IReadOnlyList<ClientAttachmentFileSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count is < 1 or > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage ||
            selections.Any(static selection => selection is null))
        {
            throw new ArgumentException(
                "One to ten attachment selections are required.",
                nameof(selections));
        }

        return selections.All(static selection => selection.IsImage)
            ? MessageType.Image
            : MessageType.File;
    }

    private static ClientAttachmentFileSelectionOutcome CreateCore(
        IReadOnlyList<string> paths,
        IReadOnlyList<ClientAttachmentFileSelection> existingSelections,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingSelection in existingSelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!knownPaths.Add(existingSelection.PathIdentity))
            {
                return ClientAttachmentFileSelectionOutcome.Failure(
                    ClientAttachmentFileSelectionStatus.DuplicateFile);
            }
        }

        var selections = new List<ClientAttachmentFileSelection>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = TryCreateSelection(path, knownPaths, out var selection);
            if (status != ClientAttachmentFileSelectionStatus.Success)
            {
                return ClientAttachmentFileSelectionOutcome.Failure(status);
            }

            cancellationToken.ThrowIfCancellationRequested();
            selections.Add(selection!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ClientAttachmentFileSelectionOutcome.Success(selections.AsReadOnly());
    }

    private static ClientAttachmentFileSelectionStatus TryCreateSelection(
        string? path,
        ISet<string> knownPaths,
        out ClientAttachmentFileSelection? selection)
    {
        selection = null;
        string normalizedPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return ClientAttachmentFileSelectionStatus.InvalidPath;
            }

            normalizedPath = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(normalizedPath) || Directory.Exists(normalizedPath))
            {
                return ClientAttachmentFileSelectionStatus.InvalidPath;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return ClientAttachmentFileSelectionStatus.InvalidPath;
        }

        if (!knownPaths.Add(normalizedPath))
        {
            return ClientAttachmentFileSelectionStatus.DuplicateFile;
        }

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return ClientAttachmentFileSelectionStatus.FileNotFound;
            }

            var originalFileName = fileInfo.Name;
            if (!ClientAttachmentMetadataPolicy.IsValidOriginalFileName(originalFileName))
            {
                return ClientAttachmentFileSelectionStatus.InvalidFileName;
            }

            using var stream = OpenFile(normalizedPath);
            var size = stream.Length;
            if (size == 0)
            {
                return ClientAttachmentFileSelectionStatus.EmptyFile;
            }

            if (size > ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize)
            {
                return ClientAttachmentFileSelectionStatus.FileTooLarge;
            }

            if (stream.ReadByte() < 0)
            {
                return ClientAttachmentFileSelectionStatus.FileUnavailable;
            }

            var classification = ResolveMimeClassification(fileInfo.Extension);
            var source = new ClientAttachmentUploadSource(
                originalFileName,
                classification.ContentType,
                size,
                token => OpenReadAsync(normalizedPath, size, token));
            selection = new ClientAttachmentFileSelection(
                Guid.NewGuid(),
                source,
                originalFileName,
                FormatDisplaySize(size),
                classification.IsImage,
                normalizedPath);
            return ClientAttachmentFileSelectionStatus.Success;
        }
        catch (FileNotFoundException)
        {
            return ClientAttachmentFileSelectionStatus.FileNotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return ClientAttachmentFileSelectionStatus.FileNotFound;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
            NotSupportedException or ArgumentException)
        {
            return ClientAttachmentFileSelectionStatus.FileUnavailable;
        }
    }

    private static async ValueTask<Stream> OpenReadAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run<Stream>(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stream = OpenFile(path);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stream.Length != expectedSize)
                        {
                            throw new IOException("The selected attachment changed after selection.");
                        }

                        return stream;
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static FileStream OpenFile(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = StreamBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    private static MimeClassification ResolveMimeClassification(string extension) =>
        MimeClassifications.TryGetValue(extension, out var classification)
            ? classification
            : new MimeClassification("application/octet-stream", false);

    private static string FormatDisplaySize(long size) =>
        size switch
        {
            < 1024 => $"{size.ToString(CultureInfo.InvariantCulture)} B",
            < 1024 * 1024 =>
                $"{(size / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KiB",
            _ => $"{(size / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MiB",
        };

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record MimeClassification(string ContentType, bool IsImage);
}
