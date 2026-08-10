namespace RelayCove.Core;

public sealed class ClientStateChangedEventArgs(ClientState state) : EventArgs
{
    public ClientState State { get; } = state ?? throw new ArgumentNullException(nameof(state));
}
