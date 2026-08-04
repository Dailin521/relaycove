using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Server.Hubs;

public interface IChatClient
{
    Task NewMessage(MessageDto message);

    Task ConversationAccessRevoked(Guid conversationId);

    Task AccountAccessRevoked(AccountAccessRevokedEvent accountAccessRevoked);
}
