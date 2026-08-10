using System.Text.Json;
using RelayCove.Core;

namespace RelayCove.App.Services;

/// <summary>Stores exactly one credential envelope in MAUI SecureStorage.</summary>
public sealed class SecureStorageCredentialVault : ICredentialVault
{
    internal const string CredentialStorageKey = "relaycove.credential-envelope.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISecureKeyValueStore _storage;

    public SecureStorageCredentialVault(ISecureKeyValueStore storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async Task<CredentialEnvelope?> GetAsync(CancellationToken cancellationToken = default)
    {
        string? serialized;
        try
        {
            serialized = await _storage.GetAsync(CredentialStorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RemoveBestEffortAsync().ConfigureAwait(false);
            return null;
        }

        if (string.IsNullOrWhiteSpace(serialized)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredEnvelope>(serialized, JsonOptions);
            if (stored is null) throw new JsonException();
            return new CredentialEnvelope(RealmEndpoint.Parse(stored.Realm), stored.Email, stored.UserId, stored.ApiKey);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            await RemoveBestEffortAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async Task SetAsync(CredentialEnvelope credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        try
        {
            var serialized = JsonSerializer.Serialize(
                new StoredEnvelope(credentials.Realm.AbsoluteUri, credentials.Email, credentials.UserId, credentials.ApiKey),
                JsonOptions);
            await _storage.SetAsync(CredentialStorageKey, serialized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RemoveBestEffortAsync().ConfigureAwait(false);
            throw new CredentialVaultException(CredentialVaultFailure.Write);
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _storage.RemoveAsync(CredentialStorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CredentialVaultException(CredentialVaultFailure.Remove);
        }
    }

    private async Task RemoveBestEffortAsync()
    {
        try
        {
            await _storage.RemoveAsync(CredentialStorageKey).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A damaged platform key is still treated as unavailable and cannot be surfaced to UI.
        }
    }

    private sealed record StoredEnvelope(string Realm, string Email, long UserId, string ApiKey)
    {
        public override string ToString() =>
            "StoredEnvelope { Realm = [redacted], Email = [redacted], UserId = [redacted], ApiKey = [redacted] }";
    }
}
