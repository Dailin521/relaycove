namespace RelayCove.Client.Activation;

internal interface IWindowsAppInstanceProvider
{
    IWindowsAppInstanceRegistration FindOrRegister(string key);
}

internal interface IWindowsAppInstanceRegistration : IDisposable
{
    event Action<WindowsProcessActivation>? Activated;

    bool IsCurrent { get; }

    uint ProcessId { get; }

    WindowsProcessActivation GetCurrentActivation();

    Task RedirectCurrentActivationAsync();
}
