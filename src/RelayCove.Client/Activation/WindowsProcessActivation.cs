namespace RelayCove.Client.Activation;

internal sealed record WindowsProcessActivation
{
    private WindowsProcessActivation(
        WindowsProcessActivationKind kind,
        string? notificationArgument)
    {
        Kind = kind;
        NotificationArgument = notificationArgument;
    }

    public WindowsProcessActivationKind Kind { get; }

    public string? NotificationArgument { get; }

    public static WindowsProcessActivation Launch() =>
        new(WindowsProcessActivationKind.Launch, notificationArgument: null);

    public static WindowsProcessActivation AppNotification(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return new WindowsProcessActivation(
            WindowsProcessActivationKind.AppNotification,
            argument);
    }

    public static WindowsProcessActivation Unsupported() =>
        new(WindowsProcessActivationKind.Unsupported, notificationArgument: null);

    public override string ToString() =>
        $"{nameof(WindowsProcessActivation)} {{ Kind = {Kind}, " +
        "NotificationArgument = [REDACTED] }";
}
