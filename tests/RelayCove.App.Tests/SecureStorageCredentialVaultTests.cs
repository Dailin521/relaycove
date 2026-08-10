using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class SecureStorageCredentialVaultTests
{
    [Fact]
    public async Task SetThenGetAsync_WhenEnvelopeIsValid_RoundTripsWithoutLeakingSecrets()
    {
        var storage = new FakeSecureStore();
        var vault = new SecureStorageCredentialVault(storage);
        var envelope = CreateEnvelope();

        await vault.SetAsync(envelope);
        var restored = await vault.GetAsync();

        Assert.NotNull(restored);
        Assert.Equal(envelope.Realm, restored.Realm);
        Assert.Equal(envelope.Email, restored.Email);
        Assert.Equal(envelope.UserId, restored.UserId);
        Assert.Equal(envelope.ApiKey, restored.ApiKey);
        Assert.DoesNotContain(envelope.ApiKey, restored.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(envelope.Email, restored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_WhenPlatformWriteFails_ClearsPartialValueAndThrowsControlledFailure()
    {
        var storage = new FakeSecureStore { ThrowOnSet = true };
        var vault = new SecureStorageCredentialVault(storage);
        var envelope = CreateEnvelope();

        var exception = await Assert.ThrowsAsync<CredentialVaultException>(() => vault.SetAsync(envelope));

        Assert.Equal(CredentialVaultFailure.Write, exception.Failure);
        Assert.True(storage.RemoveCalls > 0);
        Assert.DoesNotContain(envelope.ApiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(envelope.Email, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_WhenStoredJsonIsDamaged_RemovesItAndReturnsNull()
    {
        var storage = new FakeSecureStore { Value = "{not-json" };
        var vault = new SecureStorageCredentialVault(storage);

        var result = await vault.GetAsync();

        Assert.Null(result);
        Assert.True(storage.RemoveCalls > 0);
        Assert.Null(storage.Value);
    }

    [Fact]
    public async Task GetAsync_WhenPlatformKeyChanged_RemovesUnreadableValueAndReturnsNull()
    {
        var storage = new FakeSecureStore { ThrowOnGet = true, Value = "unreadable" };
        var vault = new SecureStorageCredentialVault(storage);

        var result = await vault.GetAsync();

        Assert.Null(result);
        Assert.True(storage.RemoveCalls > 0);
    }

    private static CredentialEnvelope CreateEnvelope() =>
        new(RealmEndpoint.Parse("https://example.test"), "person@example.test", 42, "test-api-key-not-a-real-secret");
}
