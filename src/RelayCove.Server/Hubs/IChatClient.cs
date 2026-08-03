using RelayCove.Shared.Messages;

namespace RelayCove.Server.Hubs;

public interface IChatClient
{
    Task NewMessage(MessageDto message);
}
