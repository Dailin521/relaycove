using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsSingleInstanceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldRedirect_WhenAnotherPrimaryOwnsTheKey_RedirectsAndExits(
        bool isCurrentInstance,
        bool expected) =>
        Assert.Equal(expected, RichChatInstancePolicy.ShouldRedirect(isCurrentInstance));

    [Fact]
    public void WindowsApp_WhenLaunched_UsesAppLifecycleRedirectionAndForegroundActivation()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "App.xaml.cs"));

        Assert.Contains("AppInstance.FindOrRegisterForKey", source, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryActivateMainWindow", source, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0)", source, StringComparison.Ordinal);
    }

    private static string FindWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate workspace file: {Path.Combine(parts)}");
    }
}
