namespace RelayCove.Client.Desktop;

internal interface IClientTrayIcon : IDisposable
{
    event Action? OpenRequested;

    event Action? ExitRequested;

    void Show(ClientTrayDisplay display);

    void Update(ClientTrayDisplay display);

    void ShowNotification(string title, string message);
}
