using System.Windows;

namespace RelayCove.Client.Controls;

/// <summary>
/// Carries a display-control user intent to the shell without moving business ownership
/// out of <see cref="MainWindow"/>.
/// </summary>
public sealed class ClientControlInteractionRequestedEventArgs : RoutedEventArgs
{
    public ClientControlInteractionRequestedEventArgs(
        RoutedEvent routedEvent,
        object source,
        string interaction,
        object originalSource,
        object originalEventArgs)
        : base(routedEvent, source)
    {
        Interaction = interaction;
        InteractionSource = originalSource;
        OriginalEventArgs = originalEventArgs;
    }

    public string Interaction { get; }

    public object InteractionSource { get; }

    public object OriginalEventArgs { get; }
}
