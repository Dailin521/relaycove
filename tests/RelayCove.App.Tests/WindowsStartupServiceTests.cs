using Microsoft.Win32;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class WindowsStartupServiceTests : IDisposable
{
    // Never exercise the real Run or StartupApproved keys, or launch an executable.
    private readonly string _registryPath = $@"Software\RelayCove.Tests\Startup\{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RelayCove.Tests", $"启动 test {Guid.NewGuid():N}");

    private string RunPath => $@"{_registryPath}\Run";
    private string ApprovalPath => $@"{_registryPath}\Approval";
    private string ExecutablePath => Path.Combine(_directory, "RichChat.exe");

    public WindowsStartupServiceTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(ExecutablePath, []);
    }

    [Fact]
    public void GetState_WhenNeverEnabled_DefaultsOffWithoutCreatingRegistryKeys()
    {
        var service = CreateService();

        Assert.Equal(StartupState.Disabled, service.GetState());
        service.SetEnabled(false);

        Assert.Null(Registry.CurrentUser.OpenSubKey(_registryPath));
    }

    [Fact]
    public void SetEnabled_WhenToggled_PersistsQuotedExecutableAndRemovesOnlyOwnValue()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunPath))
            key.SetValue("UnrelatedApp", "untouched");
        var service = CreateService();

        service.SetEnabled(true);
        service.SetEnabled(true);

        using (var key = Registry.CurrentUser.OpenSubKey(RunPath))
        {
            Assert.Equal($"\"{ExecutablePath}\"", key!.GetValue("RichChat"));
            Assert.Equal(RegistryValueKind.String, key.GetValueKind("RichChat"));
        }
        Assert.Equal(StartupState.Enabled, CreateService().GetState());
        Assert.Null(Registry.CurrentUser.OpenSubKey(ApprovalPath));

        service.SetEnabled(false);
        service.SetEnabled(false);

        Assert.Equal(StartupState.Disabled, CreateService().GetState());
        using var remaining = Registry.CurrentUser.OpenSubKey(RunPath);
        Assert.Equal("untouched", remaining!.GetValue("UnrelatedApp"));
        Assert.Null(remaining.GetValue("RichChat"));
    }

    [Theory]
    [InlineData(2, StartupState.Enabled)]
    [InlineData(6, StartupState.Enabled)]
    [InlineData(3, StartupState.DisabledByWindows)]
    [InlineData(7, StartupState.DisabledByWindows)]
    [InlineData(99, StartupState.UnknownWindowsApproval)]
    public void GetState_WhenWindowsOverridesStartup_ReportsApprovalWithoutOverwritingIt(int flag, StartupState expected)
    {
        var approval = new byte[12];
        BitConverter.GetBytes(flag).CopyTo(approval, 0);
        using var key = Registry.CurrentUser.CreateSubKey(ApprovalPath);
        key.SetValue("RichChat", approval, RegistryValueKind.Binary);
        var service = CreateService();

        service.SetEnabled(true);

        Assert.Equal(expected, service.GetState());
        Assert.Equal(approval, Assert.IsType<byte[]>(key.GetValue("RichChat")));
        service.SetEnabled(false);
        Assert.Equal(StartupState.Disabled, service.GetState());
        Assert.Equal(approval, Assert.IsType<byte[]>(key.GetValue("RichChat")));
    }

    [Fact]
    public void GetState_WhenWindowsApprovalMalformed_DoesNotClaimStartupIsAllowed()
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovalPath);
        key.SetValue("RichChat", new byte[] { 2 }, RegistryValueKind.Binary);
        var service = CreateService();
        service.SetEnabled(true);

        Assert.Equal(StartupState.UnknownWindowsApproval, service.GetState());
        key.SetValue("RichChat", "invalid", RegistryValueKind.String);
        Assert.Equal(StartupState.UnknownWindowsApproval, service.GetState());
    }

    [Fact]
    public void GetState_WhenEntryPointsAtAnotherLocation_RequiresExplicitEnableToReplaceIt()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        const string oldCommand = "\"C:\\old location\\RichChat.exe\"";
        key.SetValue("RichChat", oldCommand);
        var service = CreateService();

        Assert.Equal(StartupState.DifferentExecutable, service.GetState());
        Assert.Equal(oldCommand, key.GetValue("RichChat"));

        service.SetEnabled(true);
        Assert.Equal(StartupState.Enabled, service.GetState());
        Assert.Equal($"\"{ExecutablePath}\"", key.GetValue("RichChat"));
    }

    [Fact]
    public void SetEnabled_WhenExecutableMissing_RejectsRegistration()
    {
        var service = new WindowsStartupService(Path.Combine(_directory, "missing.exe"), RunPath, ApprovalPath);

        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true));
        Assert.Null(Registry.CurrentUser.OpenSubKey(RunPath));
    }

    private WindowsStartupService CreateService() => new(ExecutablePath, RunPath, ApprovalPath);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
        File.Delete(ExecutablePath);
        Directory.Delete(_directory, recursive: false);
    }
}
