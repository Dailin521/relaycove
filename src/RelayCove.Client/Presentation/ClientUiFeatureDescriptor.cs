namespace RelayCove.Client.Presentation;

public sealed record ClientUiFeatureDescriptor(
    ClientUiFeatureId Id,
    string DisplayName,
    ClientUiFeatureAvailability Availability,
    string NoticeText);
