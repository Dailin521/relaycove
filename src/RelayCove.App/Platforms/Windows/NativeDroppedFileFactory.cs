using RelayCove.App.Services;

namespace RelayCove.App.Platforms.Windows;

internal static class NativeDroppedFileFactory
{
    internal static Task<IReadOnlyList<SelectedAttachmentFile>> CreateAsync(
        IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<SelectedAttachmentFile>>(() =>
    {
        if (paths.Count == 0) throw new InvalidOperationException("No files were supplied.");
        var selected = new List<SelectedAttachmentFile>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(path) || Directory.Exists(path))
                throw new InvalidOperationException("The selection must contain files only.");
            var file = new FileInfo(path);
            if (!file.Exists) throw new InvalidOperationException("A selected file is unavailable.");
            var contentType = file.Extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
            selected.Add(new SelectedAttachmentFile(file.Name, contentType, file.Length, token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }, path));
        }
        return selected;
    }, cancellationToken);
}
