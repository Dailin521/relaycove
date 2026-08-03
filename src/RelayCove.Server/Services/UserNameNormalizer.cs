namespace RelayCove.Server.Services;

public sealed class UserNameNormalizer
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 64;

    public string Normalize(string userName)
    {
        if (!TryNormalize(userName, out var normalizedUserName))
        {
            throw new ArgumentException(
                $"User names must contain {MinimumLength}-{MaximumLength} ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(userName));
        }

        return normalizedUserName;
    }

    public bool TryNormalize(string? userName, out string normalizedUserName)
    {
        normalizedUserName = string.Empty;
        if (userName is null || userName.Length is < MinimumLength or > MaximumLength)
        {
            return false;
        }

        foreach (var character in userName)
        {
            if (!IsAllowed(character))
            {
                return false;
            }
        }

        normalizedUserName = userName.ToUpperInvariant();
        return true;
    }

    private static bool IsAllowed(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '.'
        or '_'
        or '-';
}
