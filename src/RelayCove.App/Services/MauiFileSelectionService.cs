namespace RelayCove.App.Services;

public sealed class MauiFileSelectionService : IFileSelectionService
{
    public async Task<SelectedAttachmentFile?> PickAvatarAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "选择头像图片",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"]
            })
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null) return null;
        await using var probe = await result.OpenReadAsync();
        var contentType = Path.GetExtension(result.FileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => result.ContentType
        };
        return new SelectedAttachmentFile(result.FileName, contentType, probe.CanSeek ? probe.Length : 0,
            async token =>
            {
                token.ThrowIfCancellationRequested();
                return await result.OpenReadAsync();
            });
    }

    public Task<IReadOnlyList<SelectedAttachmentFile>> PickStickersAsync(CancellationToken cancellationToken = default) =>
        PickFilesAsync(new PickOptions
        {
            PickerTitle = "导入表情（最多 50 张，单张不超过 25 MiB）",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".png", ".jpg", ".jpeg", ".webp", ".gif"]
            })
        }, cancellationToken);

    public Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default) =>
        PickFilesAsync(new PickOptions { PickerTitle = "选择最多 10 个附件" }, cancellationToken);

    private static async Task<IReadOnlyList<SelectedAttachmentFile>> PickFilesAsync(
        PickOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await FilePicker.Default.PickMultipleAsync(options);
        if (results is null) return [];
        var selected = new List<SelectedAttachmentFile>();
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) continue;
            await using var probe = await result.OpenReadAsync();
            var length = probe.CanSeek ? probe.Length : 0;
            selected.Add(new SelectedAttachmentFile(
                result.FileName,
                result.ContentType,
                length,
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    return await result.OpenReadAsync();
                },
                result.FullPath));
        }
        return selected;
    }
}
