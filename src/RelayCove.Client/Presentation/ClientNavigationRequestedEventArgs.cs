using System.Windows;

namespace RelayCove.Client.Presentation;

public sealed class ClientNavigationRequestedEventArgs : RoutedEventArgs
{
    public ClientNavigationRequestedEventArgs(
        RoutedEvent routedEvent,
        object source,
        ClientNavigationSection section)
        : base(routedEvent, source)
    {
        Section = section;
    }

    public ClientNavigationSection Section { get; }
}
