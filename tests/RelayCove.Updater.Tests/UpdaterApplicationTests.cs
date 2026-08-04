namespace RelayCove.Updater.Tests;

public sealed class UpdaterApplicationTests
{
    [Fact]
    public void Run_WhenClientHasExited_AppliesPackageAndStartsOnlyFixedClient()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe"));
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        var result = UpdaterApplication.Run(arguments, fake);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(target, "RelayCove.Client.exe")));
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
        Assert.Equal(Path.Combine(target, "RelayCove.Client.exe"), fake.StartedExecutablePath);
        Assert.Empty(fake.StartedArguments);
    }

    [Fact]
    public void Run_WhenPidIdentityDoesNotMatch_RejectsBeforeReplace()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe")) { IsRunning = true };
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        var result = UpdaterApplication.Run(arguments, fake);

        Assert.Equal((int)UpdaterExitCode.ValidationFailed, result);
        Assert.True(File.Exists(Path.Combine(target, "old.txt")));
        Assert.Null(fake.StartedExecutablePath);
    }

    [Fact]
    public void Run_WhenFixedClientCannotStart_RestoresOldTarget()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe")) { ThrowOnStart = true };
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        var result = UpdaterApplication.Run(arguments, fake);

        Assert.Equal((int)UpdaterExitCode.ApplyFailed, result);
        Assert.True(File.Exists(Path.Combine(target, "old.txt")));
        Assert.Equal("old-client", File.ReadAllText(Path.Combine(target, "RelayCove.Client.exe")));
    }

    [Fact]
    public void Run_WhenPreparedJournalDidNotMoveTarget_ClearsJournalAndContinues()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target);
        File.WriteAllText(Path.Combine(temporary.Path, ".RelayCove.relaycove-update.json"), "{\"state\":\"prepared\"}");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe"));
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        Assert.Equal(0, UpdaterApplication.Run(arguments, fake));
        Assert.False(File.Exists(Path.Combine(temporary.Path, ".RelayCove.relaycove-update.json")));
    }

    [Fact]
    public void Run_WhenInstalledManifestVersionDoesNotMatch_LeavesTargetUnchanged()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target, "0.9.0");
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe"));
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        var result = UpdaterApplication.Run(arguments, fake);

        Assert.Equal((int)UpdaterExitCode.ValidationFailed, result);
        Assert.Equal("old-client", File.ReadAllText(Path.Combine(target, "RelayCove.Client.exe")));
        Assert.True(File.Exists(Path.Combine(target, "old.txt")));
        Assert.Null(fake.StartedExecutablePath);
    }

    [Fact]
    public void Run_WhenActivatedCrashLeftNewTarget_RestoresOldThenReappliesAndStarts()
    {
        using var temporary = new TemporaryDirectory();
        var target = Path.Combine(temporary.Path, "RelayCove");
        var backup = Path.Combine(temporary.Path, ".RelayCove.relaycove-backup");
        Directory.CreateDirectory(target);
        WriteInstalledClient(target, "1.0.1-rc.1", "unlaunched-new-client");
        Directory.CreateDirectory(backup);
        WriteInstalledClient(backup);
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old");
        File.WriteAllText(Path.Combine(temporary.Path, ".RelayCove.relaycove-update.json"), "{\"state\":\"activated\"}");
        var archive = PackageFixture.Create(temporary.Path);
        var fake = new FakePlatform(Path.Combine(temporary.Path, "external", "RelayCove.Updater.exe"));
        File.WriteAllText(fake.ExecutablePath, "fake");
        var arguments = TestArguments.Create();
        Replace(arguments, "--archive", archive.Path);
        Replace(arguments, "--expected-sha256", archive.Hash);
        Replace(arguments, "--expected-size", archive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Replace(arguments, "--target", target);
        arguments = [.. arguments, "--bootstrapped"];

        var result = UpdaterApplication.Run(arguments, fake);

        Assert.Equal((int)UpdaterExitCode.Success, result);
        Assert.Equal("client", File.ReadAllText(Path.Combine(target, "RelayCove.Client.exe")));
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, ".RelayCove.relaycove-quarantine")));
        Assert.False(File.Exists(Path.Combine(temporary.Path, ".RelayCove.relaycove-update.json")));
        Assert.Equal(Path.Combine(target, "RelayCove.Client.exe"), fake.StartedExecutablePath);
    }

    private static void WriteInstalledClient(string target, string version = "1.0.0", string content = "old-client")
    {
        File.WriteAllText(Path.Combine(target, "RelayCove.Client.exe"), content);
        File.WriteAllText(Path.Combine(target, "manifest.json"), $"{{\"version\":\"{version}\"}}");
    }

    private static void Replace(string[] arguments, string key, string value) => arguments[Array.IndexOf(arguments, key) + 1] = value;
}

internal sealed class FakePlatform : IUpdaterPlatform
{
    internal FakePlatform(string executablePath)
    {
        ExecutablePath = executablePath;
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
    }

    public string ExecutablePath { get; }

    internal bool IsRunning { get; set; }

    internal bool ThrowOnStart { get; set; }

    internal string? StartedExecutablePath { get; private set; }

    internal IReadOnlyList<string> StartedArguments { get; private set; } = [];

    public bool ProcessMatches(int processId, long startTimeUtcTicks) => false;

    public bool IsProcessRunning(int processId) => IsRunning;

    public void Start(string executablePath, IEnumerable<string> arguments, string workingDirectory)
    {
        StartedExecutablePath = executablePath;
        StartedArguments = arguments.ToArray();
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("start failure");
        }
    }
}
