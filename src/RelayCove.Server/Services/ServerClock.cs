using RelayCove.Server.Data;

namespace RelayCove.Server.Services;

public sealed class ServerClock(TimeProvider timeProvider)
{
    public DateTime UtcNow => SqliteValueConverters.NormalizeUtc(
        timeProvider.GetUtcNow().UtcDateTime,
        nameof(TimeProvider));
}
