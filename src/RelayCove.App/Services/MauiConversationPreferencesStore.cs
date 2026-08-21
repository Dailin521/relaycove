using System.Text.Json;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class MauiConversationPreferencesStore : IConversationPreferencesStore
{
    private const string PreferenceKey = "relaycove.conversation-preferences.v1";
    private readonly Dictionary<string, ConversationPreference> _preferences;

    public MauiConversationPreferencesStore()
    {
        try
        {
            _preferences = JsonSerializer.Deserialize<Dictionary<string, ConversationPreference>>(
                Preferences.Default.Get(PreferenceKey, string.Empty)) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _preferences = new(StringComparer.Ordinal);
        }
    }

    public ConversationPreference Get(AccountId accountId, string conversationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        return _preferences.GetValueOrDefault(CreateKey(accountId, conversationKey)) ?? new ConversationPreference();
    }

    public void Save(AccountId accountId, string conversationKey, ConversationPreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentNullException.ThrowIfNull(preference);
        var key = CreateKey(accountId, conversationKey);
        var normalized = preference with
        {
            Remark = string.IsNullOrWhiteSpace(preference.Remark) ? null : preference.Remark.Trim()
        };
        if (normalized == new ConversationPreference()) _preferences.Remove(key);
        else _preferences[key] = normalized;
        Preferences.Default.Set(PreferenceKey, JsonSerializer.Serialize(_preferences));
    }

    private static string CreateKey(AccountId accountId, string conversationKey) =>
        $"{accountId.Value}:{conversationKey}";
}
