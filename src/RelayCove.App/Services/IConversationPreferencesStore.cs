using RelayCove.Core;

namespace RelayCove.App.Services;

public interface IConversationPreferencesStore
{
    ConversationPreference Get(AccountId accountId, string conversationKey);
    void Save(AccountId accountId, string conversationKey, ConversationPreference preference);
}
