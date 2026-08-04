using RelayCove.Shared.Updates;

namespace RelayCove.Updater.Tests;

public sealed class UpdateLayoutRecoveryTests
{
    [Fact]
    public void AcquireLock_WhenStaleFileExists_AcquiresAndDeletesOnClose()
    {
        using var fixture = new LayoutFixture();
        File.WriteAllText(fixture.LockPath, "stale");

        using (fixture.Layout.AcquireLock())
        {
            Assert.True(File.Exists(fixture.LockPath));
        }

        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public void AcquireLock_WhenAnotherHandleIsActive_RejectsSecondOwner()
    {
        using var fixture = new LayoutFixture();
        using var activeLock = fixture.Layout.AcquireLock();

        Assert.Throws<IOException>(() => fixture.Layout.AcquireLock());
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("activated")]
    public void RecoverIfNecessary_WhenTargetExistsWithoutBackup_ClearsCompletedJournal(string state)
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("target");
        fixture.WriteJournal(state);

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("target", fixture.ReadTargetMarker());
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenPreparedMoveLostTarget_RestoresOnlyBackup()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateBackup("old");
        fixture.WriteJournal("prepared");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("old", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenActivatedBeforeLaunchCrashed_RestoresBackup()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        fixture.WriteJournal("activated");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("old", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(Directory.Exists(fixture.QuarantinePath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenCommittingCleanupCrashed_KeepsTargetAndRemovesBackup()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        fixture.WriteJournal("committing");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("new", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenLaunchIntentWasDurable_KeepsNewTarget()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        fixture.WriteJournal("launching");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("new", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenPreparedActivationReachedBothDirectories_RestoresBackup()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        fixture.WriteJournal("prepared");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("old", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(Directory.Exists(fixture.QuarantinePath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenJournalIsTruncated_LeavesAllDirectoriesUntouched()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        File.WriteAllText(fixture.JournalPath, "{\"state\":");

        Assert.Throws<InvalidDataException>(() => fixture.Layout.RecoverIfNecessary());

        Assert.Equal("new", fixture.ReadTargetMarker());
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.BackupPath, "marker.txt")));
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenRestoringStarts_PreservesBackupUntilOldTargetIsRestored()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("new");
        fixture.CreateBackup("old");
        fixture.WriteJournal("restoring");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("old", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.False(Directory.Exists(fixture.QuarantinePath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenOldTargetWasRestored_CleansExactQuarantine()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateTarget("old");
        fixture.CreateQuarantine("new");
        fixture.WriteJournal("restoring");

        fixture.Layout.RecoverIfNecessary();

        Assert.Equal("old", fixture.ReadTargetMarker());
        Assert.False(Directory.Exists(fixture.QuarantinePath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void RecoverIfNecessary_WhenOnlyQuarantineSurvives_DoesNotDeleteUniqueCopy()
    {
        using var fixture = new LayoutFixture();
        fixture.CreateQuarantine("new");
        fixture.WriteJournal("restoring");

        Assert.Throws<InvalidDataException>(() => fixture.Layout.RecoverIfNecessary());

        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.QuarantinePath, "marker.txt")));
        Assert.True(File.Exists(fixture.JournalPath));
    }
}

internal sealed class LayoutFixture : IDisposable
{
    private readonly TemporaryDirectory temporary = new();

    internal LayoutFixture()
    {
        TargetPath = Path.Combine(temporary.Path, "RelayCove");
        BackupPath = Path.Combine(temporary.Path, ".RelayCove.relaycove-backup");
        QuarantinePath = Path.Combine(temporary.Path, ".RelayCove.relaycove-quarantine");
        JournalPath = Path.Combine(temporary.Path, ".RelayCove.relaycove-update.json");
        LockPath = Path.Combine(temporary.Path, ".RelayCove.relaycove-update.lock");
        var externalDirectory = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(externalDirectory);
        var executablePath = Path.Combine(externalDirectory, "RelayCove.Updater.exe");
        File.WriteAllText(executablePath, "updater");
        var options = new UpdaterOptions
        {
            ArchivePath = Path.Combine(temporary.Path, "release.zip"),
            ExpectedSha256 = new string('a', 64),
            ExpectedSize = 1,
            ExpectedVersion = SemanticVersion.Parse("1.0.1"),
            CurrentVersion = SemanticVersion.Parse("1.0.0"),
            TargetPath = TargetPath,
            WaitProcessId = 1,
            WaitProcessStartTimeUtcTicks = 1,
            WaitTimeoutSeconds = 1,
            BootstrapToken = "1234567890abcdef1234567890abcdef",
            Bootstrapped = true,
        };
        Layout = UpdateLayout.Create(options, executablePath);
    }

    internal UpdateLayout Layout { get; }

    internal string TargetPath { get; }

    internal string BackupPath { get; }

    internal string QuarantinePath { get; }

    internal string JournalPath { get; }

    internal string LockPath { get; }

    internal void CreateTarget(string marker) => CreateDirectory(TargetPath, marker);

    internal void CreateBackup(string marker) => CreateDirectory(BackupPath, marker);

    internal void CreateQuarantine(string marker) => CreateDirectory(QuarantinePath, marker);

    internal void WriteJournal(string state) => File.WriteAllText(JournalPath, $"{{\"state\":\"{state}\"}}");

    internal string ReadTargetMarker() => File.ReadAllText(Path.Combine(TargetPath, "marker.txt"));

    public void Dispose() => temporary.Dispose();

    private static void CreateDirectory(string path, string marker)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "marker.txt"), marker);
    }
}
