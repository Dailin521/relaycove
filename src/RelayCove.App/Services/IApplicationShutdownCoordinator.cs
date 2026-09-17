namespace RelayCove.App.Services;

public interface IApplicationShutdownCoordinator
{
    bool IsShutdownRequested { get; }
    Task RequestShutdownAsync(ApplicationShutdownEntryPoint entryPoint);
}

public enum ApplicationShutdownEntryPoint
{
    WindowDestroyed,
    TrayExit
}
