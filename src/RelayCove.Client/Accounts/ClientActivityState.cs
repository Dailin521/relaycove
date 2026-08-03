namespace RelayCove.Client.Accounts;

internal sealed class ClientActivityState
{
    private ClientActivitySnapshot snapshot = ClientActivitySnapshot.Inactive;

    public ClientActivitySnapshot Snapshot => Volatile.Read(ref snapshot);

    public Guid? GetForegroundConversationId() => Snapshot.ForegroundConversationId;

    public void Update(ClientActivitySnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.OpenConversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An open conversation ID cannot be empty.",
                nameof(value));
        }

        Volatile.Write(ref snapshot, value);
    }
}
