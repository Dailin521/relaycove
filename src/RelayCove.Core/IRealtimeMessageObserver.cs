namespace RelayCove.Core;

public interface IRealtimeMessageObserver
{
    event EventHandler<RealtimeMessageReceivedEventArgs>? RealtimeMessageReceived;
}
