namespace RelayCove.App.Services;

public interface IFileSelectionService
{
    Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SelectedAttachmentFile>> PickStickersAsync(CancellationToken cancellationToken = default) =>
        PickMultipleAsync(cancellationToken);
    Task<SelectedAttachmentFile?> PickAvatarAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<SelectedAttachmentFile?>(new NotSupportedException("Avatar selection is not available."));
}
