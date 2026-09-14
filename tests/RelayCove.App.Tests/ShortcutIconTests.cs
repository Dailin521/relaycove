using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class ShortcutIconTests
{
    [Fact]
    public void Application_WhenBuilt_CopiesCurrentIconForShortcuts()
    {
        var source = Path.Combine(FindWorkspaceRoot(), "src", "RelayCove.App", "Resources", "AppIcon", "RelayCove.ico");
        var output = Path.Combine(AppContext.BaseDirectory, "Assets", "RichChat-R.ico");
        Assert.True(File.Exists(output));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
    }

    [Fact]
    public void Installer_WhenCreatingShortcuts_UsesPublishedIconInsteadOfCachedExecutableIcon()
    {
        var root = FindWorkspaceRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "installer", "RichChat.iss"));
        var shortcuts = installer.Split("[Icons]", StringSplitOptions.None)[1].Split("[Run]", StringSplitOptions.None)[0]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("Name:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, shortcuts.Length);
        Assert.All(shortcuts, line => Assert.Contains("IconFilename: \"{app}\\Assets\\RichChat-R.ico\"", line));

        var project = XDocument.Load(Path.Combine(root, "src", "RelayCove.App", "RelayCove.App.csproj"));
        var icon = project.Descendants("Content").Single(item => item.Attribute("Link")?.Value == "Assets\\RichChat-R.ico");
        Assert.Equal("PreserveNewest", icon.Attribute("CopyToPublishDirectory")?.Value);
        var packager = File.ReadAllText(Path.Combine(root, "scripts", "package-installer.ps1"));
        Assert.Contains("\"Assets/RichChat-R.ico\"", packager);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RelayCove.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Workspace root was not found.");
    }
}
