using System.Text.Json;

namespace RelayCove.Updater;

internal sealed class UpdateLayout
{
    private readonly string executablePath;
    private readonly string targetPath;
    private readonly string parentPath;
    private readonly string targetName;
    private readonly string backupPath;
    private readonly string journalPath;
    private readonly string lockPath;

    private UpdateLayout(string executablePath, string targetPath, string parentPath, string targetName)
    {
        this.executablePath = executablePath;
        this.targetPath = targetPath;
        this.parentPath = parentPath;
        this.targetName = targetName;
        backupPath = Path.Combine(parentPath, $".{targetName}.relaycove-backup");
        journalPath = Path.Combine(parentPath, $".{targetName}.relaycove-update.json");
        lockPath = Path.Combine(parentPath, $".{targetName}.relaycove-update.lock");
    }

    internal bool IsExecutableInsideTarget => IsInside(executablePath, targetPath);

    internal static UpdateLayout Create(UpdaterOptions options, string executablePath)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TargetPath));
        var parent = Directory.GetParent(target)?.FullName ?? throw new InvalidDataException("Target directory is invalid.");
        return new UpdateLayout(Path.GetFullPath(executablePath), target, parent, Path.GetFileName(target));
    }

    internal void ValidateInputs(string archivePath)
    {
        var clientExecutable = Path.Combine(targetPath, "RelayCove.Client.exe");
        if (!Directory.Exists(targetPath) || !File.Exists(clientExecutable) || IsVolumeRoot(targetPath) || !File.Exists(archivePath) ||
            IsInside(archivePath, targetPath) || IsReparsePath(parentPath) || IsReparsePath(targetPath) ||
            IsReparsePath(Path.GetDirectoryName(archivePath) ?? archivePath) || IsReparsePath(Path.GetDirectoryName(executablePath) ?? executablePath) ||
            IsReparseFile(archivePath) || IsReparseFile(clientExecutable))
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

    internal void RecoverIfNecessary()
    {
        if (IsVolumeRoot(targetPath) || !Directory.Exists(parentPath) || IsReparsePath(parentPath) || IsReparsePath(targetPath) ||
            (Directory.Exists(backupPath) && IsReparsePath(backupPath)) ||
            (File.Exists(journalPath) && (File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        if (!File.Exists(journalPath))
        {
            return;
        }

        var state = ReadJournalState();

        if (Directory.Exists(targetPath) && !Directory.Exists(backupPath) && state == "prepared")
        {
            File.Delete(journalPath);
            return;
        }

        if (!Directory.Exists(targetPath) && Directory.Exists(backupPath))
        {
            Directory.Move(backupPath, targetPath);
            File.Delete(journalPath);
            return;
        }

        if (Directory.Exists(targetPath) && Directory.Exists(backupPath) && state is "prepared" or "activated")
        {
            DeleteDirectorySafe(backupPath);
            File.Delete(journalPath);
            return;
        }

        throw new InvalidDataException("Update recovery state is invalid.");
    }

    internal void RestoreAfterLaunchFailure()
    {
        if (!Directory.Exists(backupPath) || !Directory.Exists(targetPath))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        DeleteDirectorySafe(targetPath);
        Directory.Move(backupPath, targetPath);
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }
    }

    internal FileStream AcquireLock() => new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    internal void DeleteLock()
    {
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    internal void CleanupStaleBootstrapDirectories()
    {
        var currentDirectory = Path.GetDirectoryName(executablePath);
        foreach (var candidate in Directory.EnumerateDirectories(parentPath, ".relaycove-updater-*").Take(16))
        {
            if (string.Equals(candidate, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(candidate);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    info.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-1) &&
                    File.Exists(Path.Combine(candidate, "RelayCove.Updater.exe")))
                {
                    Directory.Delete(candidate, true);
                }
            }
            catch (IOException)
            {
                // A concurrent or externally locked stale bootstrap is left for a later run.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best effort and never widens update authority.
            }
        }
    }

    internal string CreateStaging()
    {
        var path = Path.Combine(parentPath, $".{targetName}.relaycove-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal void Activate(string stagingPath)
    {
        if (Directory.Exists(backupPath))
        {
            throw new InvalidDataException("Previous update backup is present.");
        }

        File.WriteAllText(journalPath, JsonSerializer.Serialize(new { state = "prepared" }));
        Directory.Move(targetPath, backupPath);
        try
        {
            Directory.Move(stagingPath, targetPath);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(new { state = "activated" }));
        }
        catch
        {
            if (!Directory.Exists(targetPath) && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, targetPath);
            }

            throw;
        }
    }

    internal void Complete()
    {
        if (Directory.Exists(backupPath))
        {
            DeleteDirectorySafe(backupPath);
        }

        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
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
            if (new FileInfo(journalPath).Length > 256)
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
            if (value is not "prepared" and not "activated")
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

    private static void DeleteDirectorySafe(string path)
    {
        if (IsReparsePath(path))
        {
            throw new InvalidDataException("Update recovery state is invalid.");
        }

        Directory.Delete(path, true);
    }

    private static bool IsVolumeRoot(string path) => string.Equals(Path.GetPathRoot(path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

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
