namespace RelayCove.App.Services;

public interface IFileSelectionService
{
    Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default);
}
