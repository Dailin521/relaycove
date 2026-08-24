namespace RelayCove.Core;

public sealed class RealtimeMessageReceivedEventArgs(ChatMessage message) : EventArgs
{
    public ChatMessage Message { get; } = message ?? throw new ArgumentNullException(nameof(message));
}
