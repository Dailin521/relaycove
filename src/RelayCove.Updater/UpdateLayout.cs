using System.Text;
using System.Text.Json;

namespace RelayCove.Updater;

internal sealed class UpdateLayout
{
    private const string PreparedState = "prepared";
    private const string ActivatedState = "activated";
    private const string CommittingState = "committing";
    private const string RestoringState = "restoring";
    private readonly string executablePath;
    private readonly string targetPath;
    private readonly string parentPath;
    private readonly string targetName;
    private readonly string backupPath;
    private readonly string quarantinePath;
    private readonly string journalPath;
    private readonly string lockPath;

    private UpdateLayout(string executablePath, string targetPath, string parentPath, string targetName)
    {
        this.executablePath = executablePath;
        this.targetPath = targetPath;
        this.parentPath = parentPath;
        this.targetName = targetName;
        backupPath = Path.Combine(parentPath, $".{targetName}.relaycove-backup");
        quarantinePath = Path.Combine(parentPath, $".{targetName}.relaycove-quarantine");
        journalPath = Path.Combine(parentPath, $".{targetName}.relaycove-update.json");
        lockPath = Path.Combine(parentPath, $".{targetName}.relaycove-update.lock");
    }

    internal bool IsExecutableInsideTarget => IsInside(executablePath, targetPath);

    internal static UpdateLayout Create(UpdaterOptions options, string executablePath)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TargetPath));
        if (IsUnc(target))
        {
            throw new InvalidDataException("Target directory is invalid.");
        }

        var parent = Directory.GetParent(target)?.FullName ?? throw new InvalidDataException("Target directory is invalid.");
        return new UpdateLayout(Path.GetFullPath(executablePath), target, parent, Path.GetFileName(target));
    }

    internal void ValidateInputs(string archivePath, string currentVersion)
    {
        var clientExecutable = Path.Combine(targetPath, "RelayCove.Client.exe");
        var installedManifest = Path.Combine(targetPath, "manifest.json");
        if (!Directory.Exists(targetPath) || !File.Exists(clientExecutable) || IsVolumeRoot(targetPath) || !File.Exists(archivePath) ||
            !File.Exists(installedManifest) ||
            IsInside(archivePath, targetPath) || IsReparsePath(parentPath) || IsReparsePath(targetPath) ||
            IsReparsePath(Path.GetDirectoryName(archivePath) ?? archivePath) || IsReparsePath(Path.GetDirectoryName(executablePath) ?? executablePath) ||
            IsReparseFile(archivePath) || IsReparseFile(clientExecutable) || IsReparseFile(installedManifest) ||
            !InstalledManifestHasVersion(installedManifest, currentVersion))
        {
            throw new InvalidDataException("Update paths are invalid.");
        }

        var probe = Path.Combine(targetPath, $".relaycove-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    internal void StartBootstrap(UpdaterOptions options, IUpdaterPlatform platform)
    {
        ValidateBootstrapParent();
        var bootstrapDirectory = Path.Combine(parentPath, $".relaycove-updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bootstrapDirectory);
        var bootstrapPath = Path.Combine(bootstrapDirectory, "RelayCove.Updater.exe");
        File.Copy(executablePath, bootstrapPath, false);
        var arguments = new List<string>
        {
            "apply", "--archive", options.ArchivePath, "--expected-sha256", options.ExpectedSha256,
            "--expected-size", options.ExpectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--expected-version", options.ExpectedVersion.ToString(), "--current-version", options.CurrentVersion.ToString(),
            "--target", options.TargetPath, "--wait-pid", options.WaitProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--wait-start-time-utc-ticks", options.WaitProcessStartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--wait-timeout-seconds", options.WaitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--bootstrapped",
        };
        platform.Start(bootstrapPath, arguments, bootstrapDirectory);
    }

    internal FileStream AcquireLock()
    {
        if (!Directory.Exists(parentPath) || IsReparsePath(parentPath) || IsReparseFile(lockPath))
        {
            throw new InvalidDataException("Update lock path is invalid.");
        }

        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose);
    }

    internal void RecoverIfNecessary()
    {
        ValidateRecoveryPaths();
        if (!File.Exists(journalPath))
        {
            if (Directory.Exists(backupPath) || Directory.Exists(quarantinePath))
            {
                throw new InvalidDataException("Update recovery state is invalid.");
            }

            return;
        }

        var state = ReadJournalState();
        if (state is PreparedState or ActivatedState)
        {
            RecoverActivation();
            return;
        }

        if (state == CommittingState)
        {
            RecoverCommit();
            return;
        }

        RecoverRestoration();
    }

    internal string CreateStaging()
    {
        var path = Path.Combine(parentPath, $".{targetName}.relaycove-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal void Activate(string stagingPath)
    {
        if (Directory.Exists(backupPath) || Directory.Exists(quarantinePath) || File.Exists(journalPath))
        {
            throw new InvalidDataException("Previous update state is present.");
        }

        WriteJournal(PreparedState);
        Directory.Move(targetPath, backupPath);
        try
        {
            Directory.Move(stagingPath, targetPath);
            WriteJournal(ActivatedState);
        }
        catch
        {
            if (Directory.Exists(targetPath) && Directory.Exists(backupPath) && !Directory.Exists(quarantinePath))
            {
                WriteJournal(RestoringState);
                RecoverRestoration();
            }
            else if (!Directory.Exists(targetPath) && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, targetPath);
                ClearJournal();
            }

            throw;
        }
    }

    internal void RestoreAfterLaunchFailure()
    {
        if (!Directory.Exists(backupPath) || !Directory.Exists(targetPath) || Directory.Exists(quarantinePath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        WriteJournal(RestoringState);
        Directory.Move(targetPath, quarantinePath);
        Directory.Move(backupPath, targetPath);
        FinishRestoration();
    }

    internal void Complete()
    {
        if (!Directory.Exists(targetPath) || !Directory.Exists(backupPath) || Directory.Exists(quarantinePath))
        {
            throw new InvalidDataException("Update commit state is invalid.");
        }

        WriteJournal(CommittingState);
        DeleteDirectorySafe(backupPath);
        ClearJournal();
    }

    private void RecoverCommit()
    {
        var targetExists = Directory.Exists(targetPath);
        var backupExists = Directory.Exists(backupPath);
        if (Directory.Exists(quarantinePath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        if (targetExists && backupExists)
        {
            DeleteDirectorySafe(backupPath);
            ClearJournal();
            return;
        }

        if (targetExists && !backupExists)
        {
            ClearJournal();
            return;
        }

        if (!targetExists && backupExists)
        {
            Directory.Move(backupPath, targetPath);
            ClearJournal();
            return;
        }

        throw new InvalidDataException("Update recovery state is invalid.");
    }

    private void RecoverActivation()
    {
        var targetExists = Directory.Exists(targetPath);
        var backupExists = Directory.Exists(backupPath);
        if (Directory.Exists(quarantinePath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        if (targetExists && !backupExists)
        {
            ClearJournal();
            return;
        }

        if (!targetExists && backupExists)
        {
            Directory.Move(backupPath, targetPath);
            ClearJournal();
            return;
        }

        if (targetExists && backupExists)
        {
            WriteJournal(RestoringState);
            RecoverRestoration();
            return;
        }

        throw new InvalidDataException("Update recovery state is invalid.");
    }

    private void RecoverRestoration()
    {
        var targetExists = Directory.Exists(targetPath);
        var backupExists = Directory.Exists(backupPath);
        var quarantineExists = Directory.Exists(quarantinePath);

        if (targetExists && backupExists && !quarantineExists)
        {
            Directory.Move(targetPath, quarantinePath);
            Directory.Move(backupPath, targetPath);
            FinishRestoration();
            return;
        }

        if (!targetExists && backupExists)
        {
            Directory.Move(backupPath, targetPath);
            FinishRestoration();
            return;
        }

        if (targetExists && !backupExists)
        {
            FinishRestoration();
            return;
        }

        throw new InvalidDataException("Update recovery state is invalid.");
    }

    private void FinishRestoration()
    {
        if (!Directory.Exists(targetPath) || Directory.Exists(backupPath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        if (Directory.Exists(quarantinePath) && !TryDeleteDirectorySafe(quarantinePath))
        {
            throw new IOException("Update quarantine cleanup failed.");
        }

        ClearJournal();
    }

    private void ValidateRecoveryPaths()
    {
        if (IsVolumeRoot(targetPath) || !Directory.Exists(parentPath) || IsReparsePath(parentPath) || IsReparsePath(targetPath) ||
            IsReparsePath(backupPath) || IsReparsePath(quarantinePath) || IsReparseFile(journalPath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }
    }

    private void ValidateBootstrapParent()
    {
        if (IsReparsePath(parentPath) || IsReparsePath(targetPath) || !Directory.Exists(parentPath))
        {
            throw new InvalidDataException("Update paths are invalid.");
        }
    }

    private string ReadJournalState()
    {
        try
        {
            if (new FileInfo(journalPath).Length is <= 0 or > 256)
            {
                throw new InvalidDataException("Update recovery state is invalid.");
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("state", out var state) ||
                state.ValueKind != JsonValueKind.String || document.RootElement.EnumerateObject().Count() != 1)
            {
                throw new InvalidDataException("Update recovery state is invalid.");
            }

            var value = state.GetString();
            if (value is not PreparedState and not ActivatedState and not CommittingState and not RestoringState)
            {
                throw new InvalidDataException("Update recovery state is invalid.");
            }

            return value;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }
    }

    private void WriteJournal(string state)
    {
        var temporaryPath = $"{journalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(new { state }));
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, journalPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private void ClearJournal()
    {
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }
    }

    private static bool TryDeleteDirectorySafe(string path)
    {
        try
        {
            DeleteDirectorySafe(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool InstalledManifestHasVersion(string manifestPath, string currentVersion)
    {
        try
        {
            var file = new FileInfo(manifestPath);
            if (file.Length is <= 0 or > 8 * 1024 * 1024)
            {
                return false;
            }

            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                string.Equals(version.GetString(), currentVersion, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectorySafe(string path)
    {
        if (IsReparsePath(path))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        Directory.Delete(path, true);
    }

    private static bool IsVolumeRoot(string path) => string.Equals(Path.GetPathRoot(path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnc(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool IsInside(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePath(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsReparseFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
