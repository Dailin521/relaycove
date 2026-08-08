using System.Windows;

namespace RelayCove.Client.Controls;

public sealed class ChatHeaderMembersRequestedEventArgs : RoutedEventArgs
{
    public ChatHeaderMembersRequestedEventArgs(RoutedEvent routedEvent, object source)
        : base(routedEvent, source)
    {
    }
}
