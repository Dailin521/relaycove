using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class InMemoryConversationPreferencesStore : IConversationPreferencesStore
{
    private readonly Dictionary<string, ConversationPreference> _preferences = new(StringComparer.Ordinal);

    public ConversationPreference Get(AccountId accountId, string conversationKey) =>
        _preferences.GetValueOrDefault($"{accountId.Value}:{conversationKey}") ?? new ConversationPreference();

    public void Save(AccountId accountId, string conversationKey, ConversationPreference preference) =>
        _preferences[$"{accountId.Value}:{conversationKey}"] = preference;
}
