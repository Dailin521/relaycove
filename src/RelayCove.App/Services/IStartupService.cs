namespace RelayCove.App.Services;

public interface IStartupService
{
    StartupState GetState();
    void SetEnabled(bool enabled);
}
