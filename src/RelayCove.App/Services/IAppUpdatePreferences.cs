namespace RelayCove.App.Services;

public interface IAppUpdatePreferences
{
    bool CheckOnStartup { get; set; }
    int LastPromptedBuildNumber { get; set; }
}
