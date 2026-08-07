using RelayCove.Shared.Messages;

namespace RelayCove.Client.Mentions;

internal static class ClientMentionPolicy
{
    public const int MaximumQueryLength = 64;
    public const int MaximumMentionCount = 20;
    public const int MinimumUserNameLength = 2;
    public const int MaximumUserNameLength = 64;

    public static bool IsValidQuery(string? query) =>
        query is { Length: <= MaximumQueryLength } &&
        query.All(IsUserNameCharacter);

    public static bool IsValidUserName(string? userName) =>
        userName is { Length: >= MinimumUserNameLength and <= MaximumUserNameLength } &&
        userName.All(IsUserNameCharacter) &&
        userName.Any(IsAsciiLetterOrDigit);

    public static bool IsValidDisplayName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) && displayName.Length <= 100;

    public static bool IsValidCandidate(
        MentionCandidateDto? candidate,
        string query) =>
        candidate is not null &&
        candidate.UserId != Guid.Empty &&
        IsValidUserName(candidate.UserName) &&
        IsValidDisplayName(candidate.DisplayName) &&
        candidate.UserName.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    public static bool ContainsToken(string? content, string? userName)
    {
        if (string.IsNullOrEmpty(content) || !IsValidUserName(userName))
        {
            return false;
        }

        var tokenLength = userName!.Length + 1;
        for (var index = 0; index <= content.Length - tokenLength; index++)
        {
            if (content[index] != '@' ||
                index > 0 && IsTokenAdjacentCharacter(content[index - 1]) ||
                !content.AsSpan(index + 1, userName.Length)
                    .Equals(userName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = index + tokenLength;
            if (end == content.Length || !IsTokenAdjacentCharacter(content[end]))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryInsertToken(
        string? content,
        int selectionStart,
        int selectionLength,
        string? userName,
        out ClientMentionTextEdit edit)
    {
        edit = ClientMentionTextEdit.Empty;
        if (content is null ||
            !IsValidUserName(userName) ||
            selectionStart < 0 ||
            selectionLength < 0 ||
            selectionStart > content.Length ||
            selectionLength > content.Length - selectionStart)
        {
            return false;
        }

        var prefix = content[..selectionStart];
        var suffix = content[(selectionStart + selectionLength)..];
        var leading = prefix.Length > 0 && IsTokenAdjacentCharacter(prefix[^1])
            ? " "
            : string.Empty;
        var trailing = suffix.Length == 0 || !char.IsWhiteSpace(suffix[0])
            ? " "
            : string.Empty;
        var insertion = $"{leading}@{userName}{trailing}";
        edit = new ClientMentionTextEdit(
            prefix + insertion + suffix,
            selectionStart + insertion.Length);
        return true;
    }

    public static bool TryCanonicalizeUserIds(
        IReadOnlyList<Guid>? userIds,
        out IReadOnlyList<Guid> canonicalUserIds)
    {
        canonicalUserIds = Array.Empty<Guid>();
        if (userIds is null || userIds.Count > MaximumMentionCount)
        {
            return false;
        }

        var values = userIds.ToArray();
        if (values.Length != userIds.Count ||
            values.Length > MaximumMentionCount ||
            values.Any(userId => userId == Guid.Empty) ||
            values.Distinct().Count() != values.Length)
        {
            return false;
        }

        Array.Sort(values);
        canonicalUserIds = Array.AsReadOnly(values);
        return true;
    }

    public static bool IsUserNameCharacter(char character) =>
        IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static bool IsTokenAdjacentCharacter(char character) =>
        IsUserNameCharacter(character) || character == '@';

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9';
}
