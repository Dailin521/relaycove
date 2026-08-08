using System.Windows;

namespace RelayCove.Client.Presentation;

public sealed class ClientUnavailableFeatureRequestedEventArgs : RoutedEventArgs
{
    public ClientUnavailableFeatureRequestedEventArgs(
        RoutedEvent routedEvent,
        object source,
        ClientUiFeatureId featureId,
        string displayName)
        : base(routedEvent, source)
    {
        FeatureId = featureId;
        DisplayName = displayName;
    }

    public ClientUiFeatureId FeatureId { get; }

    public string DisplayName { get; }
}
