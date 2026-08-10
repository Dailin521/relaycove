namespace RelayCove.Core;

public sealed record UnreadState
{
    public UnreadState(
        IReadOnlyDictionary<string, int>? counts = null,
        int? reportedTotal = null,
        bool isTruncated = false)
    {
        Counts = counts is null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(counts);
        if (reportedTotal < 0) throw new ArgumentOutOfRangeException(nameof(reportedTotal));
        ReportedTotal = reportedTotal;
        IsTruncated = isTruncated;
    }

    public IReadOnlyDictionary<string, int> Counts { get; }
    public int? ReportedTotal { get; }
    public bool IsTruncated { get; }
    public int Total => ReportedTotal ?? Counts.Values.Sum();

    public UnreadState Adjust(string conversationKey, int delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        if (delta == 0) return this;
        var counts = new Dictionary<string, int>(Counts, StringComparer.Ordinal);
        counts.TryGetValue(conversationKey, out var current);
        var next = Math.Max(0, current + delta);
        if (next == 0) counts.Remove(conversationKey);
        else counts[conversationKey] = next;
        int? total = ReportedTotal is { } reported ? Math.Max(0, reported + delta) : null;
        return new UnreadState(counts, total, IsTruncated);
    }

    public UnreadState RemoveChannel(long channelId)
    {
        var prefix = $"channel:{channelId}:";
        var removed = Counts.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(pair => pair.Value);
        var counts = Counts.Where(pair => !pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        int? total = ReportedTotal is { } reported ? Math.Max(0, reported - removed) : null;
        return new UnreadState(counts, total, IsTruncated);
    }
}
