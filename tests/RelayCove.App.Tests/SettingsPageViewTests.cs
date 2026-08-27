namespace RelayCove.App.Tests;

public sealed class SettingsPageViewTests
{
    [Fact]
    public void Notifications_WhenRendered_ExposeNativeControlsAndRemovePlaceholder()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Controls", "SettingsPageView.xaml"));

        Assert.Contains("Text=\"Windows 系统通知\"", source);
        Assert.Contains("DoNotDisturb, Mode=TwoWay", source);
        Assert.Contains("SystemNotificationsEnabled, Mode=TwoWay", source);
        Assert.Contains("ShowMessagePreview, Mode=TwoWay", source);
        Assert.Contains("TaskbarFlashEnabled, Mode=TwoWay", source);
        Assert.Contains("TaskbarBadgeEnabled, Mode=TwoWay", source);
        Assert.Contains("SystemNotificationStatus", source);
        Assert.Contains("TaskbarBadgeStatus", source);
        Assert.DoesNotContain("通知、presence 与在线状态尚未进入当前原生能力门", source);
    }

    [Fact]
    public void Storage_WhenRendered_ExposesDownloadLocationAndAskEachTimeControls()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Controls", "SettingsPageView.xaml"));

        Assert.Contains("DownloadFolderPath", source);
        Assert.Contains("ChangeDownloadFolderCommand", source);
        Assert.Contains("OpenDownloadFolderCommand", source);
        Assert.Contains("AskWhereToSaveDownloads, Mode=TwoWay", source);
        Assert.Contains("同名文件不会覆盖", source);
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
