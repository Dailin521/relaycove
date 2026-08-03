namespace RelayCove.Server.Services;

public sealed class SyncRequestValidator
{
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 200;

    public IReadOnlyDictionary<string, string[]> Validate(
        long? cursor,
        long? snapshotUpperBound,
        int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!cursor.HasValue || cursor.Value < 0)
        {
            errors["cursor"] = ["A non-negative sync cursor is required."];
        }

        if (snapshotUpperBound is < 0 ||
            cursor.HasValue && snapshotUpperBound.HasValue && snapshotUpperBound.Value < cursor.Value)
        {
            errors["snapshotUpperBound"] =
                ["The snapshot upper bound must be non-negative and not less than the cursor."];
        }

        if (limit is < 1 or > MaximumLimit)
        {
            errors["limit"] = [$"The limit must be between 1 and {MaximumLimit}."];
        }

        return errors;
    }
}
