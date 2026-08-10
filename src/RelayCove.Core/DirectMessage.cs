namespace RelayCove.Core;

public sealed record DirectMessage : ConversationKey
{
    private readonly IReadOnlyList<long> _otherUserIds;

    public DirectMessage(IEnumerable<long> otherUserIds)
    {
        ArgumentNullException.ThrowIfNull(otherUserIds);
        var normalized = otherUserIds.Distinct().OrderBy(id => id).ToArray();
        if (normalized.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(otherUserIds));
        _otherUserIds = Array.AsReadOnly(normalized);
    }

    public IReadOnlyList<long> OtherUserIds => _otherUserIds;
    public override string CanonicalKey => _otherUserIds.Count == 0 ? "dm:self" : $"dm:{string.Join(',', _otherUserIds)}";

    public bool Equals(DirectMessage? other) =>
        other is not null && _otherUserIds.SequenceEqual(other._otherUserIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(DirectMessage));
        foreach (var userId in _otherUserIds) hash.Add(userId);
        return hash.ToHashCode();
    }
}
