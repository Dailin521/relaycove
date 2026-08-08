using System.Windows;

namespace RelayCove.Client.Controls;

public sealed class ChatHeaderSearchRequestedEventArgs : RoutedEventArgs
{
    public ChatHeaderSearchRequestedEventArgs(RoutedEvent routedEvent, object source)
        : base(routedEvent, source)
    {
    }
}
