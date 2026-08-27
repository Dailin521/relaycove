namespace RelayCove.App.Platforms.Windows;

internal static class RichChatInstancePolicy
{
    internal const string InstanceKey = "RichChat.Main";

    internal static bool ShouldRedirect(bool isCurrentInstance) => !isCurrentInstance;
}
