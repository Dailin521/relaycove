namespace RelayCove.App.Services;

public sealed class MauiFileSelectionService : IFileSelectionService
{
    public async Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "选择最多 10 个附件"
        });
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
