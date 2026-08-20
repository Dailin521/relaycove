namespace RelayCove.App.Tests;

public sealed class DetailsPaneViewTests
{
    [Fact]
    public void DetailsPane_WhenRendered_ShowsReliableStateCapabilitiesAndChannelActionBoundary()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "DetailsPaneView.xaml"));

        Assert.Contains("DetailsKindLabel", source);
        Assert.Contains("DetailsIdentifierLabel", source);
        Assert.Contains("DetailsStateLabel", source);
        Assert.Contains("Text=\"已接通能力\"", source);
        Assert.Contains("DetailsAvailableMessage", source);
        Assert.Contains("Text=\"能力边界\"", source);
        Assert.Contains("DetailsUnavailableMessage", source);
        Assert.Contains("IsVisible=\"{Binding ShowChannelDetails}\"", source);
        Assert.Contains("IsVisible=\"{Binding ShowChannelActionBoundary}\"", source);
        Assert.Contains("IsEnabled=\"{Binding CanManageSelectedChannel}\"", source);
        Assert.Contains("IsEnabled=\"{Binding CanUnsubscribeSelectedChannel}\"", source);
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
