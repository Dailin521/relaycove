namespace RelayCove.Core;

public interface IUserActivitySource
{
    // Null means the platform could not determine activity; retain the last observation.
    bool? IsIdle { get; }
}
