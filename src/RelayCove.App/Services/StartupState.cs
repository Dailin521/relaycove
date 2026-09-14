namespace RelayCove.App.Services;

public enum StartupState
{
    Disabled,
    Enabled,
    DifferentExecutable,
    DisabledByWindows,
    UnknownWindowsApproval
}
