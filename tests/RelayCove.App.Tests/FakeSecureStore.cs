using RelayCove.App.Services;

namespace RelayCove.App.Tests;

internal sealed class FakeSecureStore : ISecureKeyValueStore
{
    public string? Value { get; set; }
    public bool ThrowOnGet { get; set; }
    public bool ThrowOnSet { get; set; }
    public int RemoveCalls { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet) throw new InvalidOperationException("platform read failed");
        return Task.FromResult(Value);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSet) throw new InvalidOperationException("platform write failed");
        Value = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        RemoveCalls++;
        Value = null;
        return Task.CompletedTask;
    }
}
