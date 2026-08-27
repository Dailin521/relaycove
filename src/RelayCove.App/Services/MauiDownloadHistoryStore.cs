using System.Text.Json;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class MauiDownloadHistoryStore : IDownloadHistoryStore
{
    private const string PreferencePrefix = "relaycove.download.history.v1.";
    private const int MaximumEntries = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<DownloadHistoryEntry> Load(AccountId accountId)
    {
        try
        {
            var serialized = Preferences.Default.Get(PreferencePrefix + accountId.Value, string.Empty);
            if (string.IsNullOrWhiteSpace(serialized)) return [];
            var entries = JsonSerializer.Deserialize<DownloadHistoryEntry[]>(serialized, JsonOptions) ?? [];
            return entries
                .Where(IsValid)
                .OrderByDescending(static entry => entry.CompletedAt)
                .Take(MaximumEntries)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    public void Save(AccountId accountId, IReadOnlyList<DownloadHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries
            .Where(IsValid)
            .OrderByDescending(static entry => entry.CompletedAt)
            .Take(MaximumEntries)
            .ToArray();
        if (normalized.Length == 0)
        {
            Preferences.Default.Remove(PreferencePrefix + accountId.Value);
            return;
        }
        Preferences.Default.Set(
            PreferencePrefix + accountId.Value,
            JsonSerializer.Serialize(normalized, JsonOptions));
    }

    private static bool IsValid(DownloadHistoryEntry entry) =>
        entry.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(entry.FileName) &&
        Path.IsPathFullyQualified(entry.FilePath) &&
        entry.Length >= 0 &&
        entry.CompletedAt != default;
}
