namespace RelayCove.App.Services;

public interface IFileSaveService
{
    Task<bool> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);
}
