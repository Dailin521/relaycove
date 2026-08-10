namespace RelayCove.App.Services;

/// <summary>Small SecureStorage seam that keeps platform calls out of tests and view models.</summary>
public interface ISecureKeyValueStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
