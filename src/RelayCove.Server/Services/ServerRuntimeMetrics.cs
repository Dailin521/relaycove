using System.Collections.Concurrent;

namespace RelayCove.Server.Services;

public sealed class ServerRuntimeMetrics(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, byte> activeConnections = new(StringComparer.Ordinal);
    private ErrorSnapshot? lastError;

    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();

    public int OnlineConnectionCount => activeConnections.Count;

    public void RecordConnection(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId))
        {
            activeConnections.TryAdd(connectionId, 0);
        }
    }

    public void RemoveConnection(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId))
        {
            activeConnections.TryRemove(connectionId, out _);
        }
    }

    public void RecordError(string category, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        Volatile.Write(ref lastError, new ErrorSnapshot(category, occurredAt));
    }

    public ErrorSnapshot? GetLastError() => Volatile.Read(ref lastError);

    public sealed record ErrorSnapshot(string Category, DateTimeOffset OccurredAt);
}
