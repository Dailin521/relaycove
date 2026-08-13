namespace RelayCove.App.Services;

public interface IWindowShellAdapter
{
    event EventHandler? StateChanged;
    bool IsPinned { get; }
    void Attach(Window window);
    void TogglePinned();
}
