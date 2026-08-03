namespace RelayCove.Server.Services;

public sealed class MentionCandidateQueryValidator
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 50;
    public const int MaximumQueryLength = UserNameNormalizer.MaximumLength;

    public IReadOnlyDictionary<string, string[]> Validate(string? query, int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!IsValidQuery(query))
        {
            errors["query"] =
                [$"The query must contain 1 to {MaximumQueryLength} user-name characters."];
        }

        if (limit is < 1 or > MaximumLimit)
        {
            errors["limit"] = [$"The limit must be between 1 and {MaximumLimit}."];
        }

        return errors;
    }

    public static bool IsValidQuery(string? query)
    {
        if (query is null || query.Length is < 1 or > MaximumQueryLength)
        {
            return false;
        }

        return query.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');
    }
}
