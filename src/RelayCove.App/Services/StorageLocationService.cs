using System.Security.Cryptography;
using System.Text.Json;

namespace RelayCove.App.Services;

/// <summary>Prepares paths before any session or storage service is constructed.</summary>
public sealed class StorageLocationService
{
    private static readonly string[] DataFolders = ["accounts", "sticker-cache", "sticker-favorites", "updates"];
    private readonly string defaultData;
    private readonly string defaultCache;
    private readonly string configurationPath;
    private Configuration configuration = new(null, null, null);

    public StorageLocationService(string defaultData, string defaultCache)
    {
        this.defaultData = Path.GetFullPath(defaultData);
        this.defaultCache = Path.GetFullPath(defaultCache);
        configurationPath = Path.Combine(this.defaultData, "storage-location.json");
    }

    public string DataPath => configuration.Root is null ? defaultData : Path.Combine(configuration.Root, "Data");
    public string CachePath => configuration.Root is null ? defaultCache : Path.Combine(configuration.Root, "Cache");
    public string DisplayPath => configuration.Root ?? defaultData;
    public string? PendingPath => configuration.Pending;
    public string Status { get; private set; } = string.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(configurationPath))
        {
            configuration = JsonSerializer.Deserialize<Configuration>(
                await File.ReadAllTextAsync(configurationPath, cancellationToken))
                ?? throw new IOException("缓存位置配置无法读取。");
            foreach (var path in new[] { configuration.Root, configuration.Pending })
                if (path is not null && !Path.IsPathFullyQualified(path))
                    throw new IOException("缓存位置配置无效。");
        }
    }

    public async Task RequestMoveAsync(string parent, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetFullPath(parent), "RichChatData");
        ValidateTarget(target);
        // Probe only the selected existing parent; never create a missing drive or fallback location.
        if (!Directory.Exists(parent)) throw new IOException("请选择可用的本地文件夹。");
        var probe = Path.Combine(parent, ".richchat-write-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(probe, string.Empty, cancellationToken);
        File.Delete(probe);
        var next = configuration with { Pending = target, MigrationId = Guid.NewGuid().ToString("N") };
        await SaveAsync(next, cancellationToken);
        configuration = next;
        Status = "已设置新目录。请从托盘退出 RichChat 后重新打开，启动时会迁移已有数据。";
    }

    public async Task CancelMoveAsync(CancellationToken cancellationToken = default)
    {
        var next = configuration with { Pending = null, MigrationId = null };
        await SaveAsync(next, cancellationToken);
        configuration = next;
        Status = "已取消目录更改，继续使用当前目录。";
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.Root is not null)
        {
            EnsureOrdinaryPath(configuration.Root);
            EnsureOrdinaryPath(DataPath);
            EnsureOrdinaryPath(CachePath);
            if (!Directory.Exists(DataPath) || !Directory.Exists(CachePath))
                throw new IOException("缓存目录不可用，请连接对应磁盘后重新打开 RichChat。");
        }
        if (configuration.Pending is not { } target) return;
        try
        {
            await MigrateAsync(target, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            // Keep both the original paths and pending request. Settings allows cancellation/reselection.
            Status = "缓存迁移未完成，仍使用原目录。请检查目标空间、权限或目录冲突，重启可重试，也可取消更改。";
        }
    }

    private async Task MigrateAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(target, allowExisting: true);
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new IOException("目标磁盘不可用。");
        if (!Guid.TryParseExact(configuration.MigrationId, "N", out _)) throw new IOException("迁移配置无效。");
        var stage = Path.Combine(parent, ".RichChat-migration-" + configuration.MigrationId);
        var files = new List<(string Source, string Relative, byte[] Hash)>();
        var oldData = DataPath;
        var oldCache = CachePath;
        var committed = false;
        try
        {
            if (Directory.Exists(stage)) RemoveStage();
            var markerPath = Path.Combine(target, ".migration.json");
            if (Directory.Exists(target))
            {
                var marker = JsonSerializer.Deserialize<Migration>(await File.ReadAllTextAsync(markerPath, cancellationToken));
                if (marker is null || string.IsNullOrEmpty(configuration.MigrationId) || marker.Id != configuration.MigrationId
                    || marker.SourceData != oldData || marker.SourceCache != oldCache || marker.Files is null)
                    throw new IOException("迁移目录不匹配。");
                var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in DataFolders) CollectFiles(Path.Combine(oldData, folder), sources);
                CollectFiles(Path.Combine(oldCache, "notification-avatars"), sources);
                var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in marker.Files)
                {
                    if (file is null || string.IsNullOrWhiteSpace(file.Relative) || file.Hash is null || file.Hash.Length != 64)
                        throw new IOException("迁移清单无效。");
                    if (Path.IsPathRooted(file.Relative) || file.Relative.Split(Path.DirectorySeparatorChar).Contains("..")
                        || !(file.Relative.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            || file.Relative.StartsWith("Cache" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                        throw new IOException("迁移清单无效。");
                    var source = file.Relative.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        ? Path.Combine(oldData, file.Relative[5..]) : Path.Combine(oldCache, file.Relative[6..]);
                    recorded.Add(source);
                    EnsureOrdinaryPath(source);
                    EnsureOrdinaryPath(Path.Combine(target, file.Relative));
                    if (!sources.Contains(source) || !IsWithin(Path.GetFullPath(Path.Combine(target, file.Relative)), target))
                        throw new IOException("迁移清单超出缓存范围。");
                    var hash = Convert.FromHexString(file.Hash);
                    await VerifyAsync(source, hash);
                    await VerifyAsync(Path.Combine(target, file.Relative), hash);
                    files.Add((source, file.Relative, hash));
                }
                if (!sources.SetEquals(recorded)) throw new IOException("迁移源已变化，请重新选择目录。");
            }
            else
            {
                Directory.CreateDirectory(stage);
                await File.WriteAllTextAsync(Path.Combine(stage, ".owner"), configuration.MigrationId, cancellationToken);
                Directory.CreateDirectory(Path.Combine(stage, "Data"));
                Directory.CreateDirectory(Path.Combine(stage, "Cache"));
                foreach (var folder in DataFolders)
                    await CopyDirectoryAsync(Path.Combine(oldData, folder), Path.Combine("Data", folder));
                await CopyDirectoryAsync(Path.Combine(oldCache, "notification-avatars"), Path.Combine("Cache", "notification-avatars"));
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOrdinaryPath(target);
                var migration = new Migration(configuration.MigrationId!, oldData, oldCache,
                    files.Select(file => new MigratedFile(file.Relative, Convert.ToHexString(file.Hash))).ToArray());
                await File.WriteAllTextAsync(Path.Combine(stage, ".migration.json"), JsonSerializer.Serialize(migration), cancellationToken);
                Directory.Move(stage, target); // Never merge with an existing directory.
            }
            var next = new Configuration(target, null, null);
            await SaveAsync(next, cancellationToken);
            configuration = next;
            committed = true;
            Status = "缓存已迁移到新目录。";
            // Delete only the exact files copied and verified, after the path switch is durable.
            foreach (var file in files)
            {
                try
                {
                    EnsureOrdinaryPath(file.Source);
                    await using (var source = File.OpenRead(file.Source))
                    {
                        var hash = await SHA256.HashDataAsync(source, cancellationToken);
                        if (!hash.AsSpan().SequenceEqual(file.Hash)) throw new IOException("原文件已变化。");
                    }
                    File.Delete(file.Source);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Status = "缓存已迁移到新目录，部分旧缓存文件未能清理，不影响使用。";
                }
            }
        }
        finally
        {
            // A failed copy only removes our unique staging tree, never either user data root.
            if (!committed && Directory.Exists(stage))
            {
                try
                {
                    RemoveStage();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        void RemoveStage()
        {
            // The exact path is derived from the persisted request ID, under the selected parent.
            if (!IsWithin(stage, parent) || Path.GetFileName(stage) != ".RichChat-migration-" + configuration.MigrationId)
                throw new IOException("迁移临时目录无效。");
            EnsureOrdinaryPath(stage);
            if (File.ReadAllText(Path.Combine(stage, ".owner")) != configuration.MigrationId)
                throw new IOException("迁移临时目录不匹配。");
            var stageFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectFiles(stage, stageFiles);
            Directory.Delete(stage, true);
        }

        async Task CopyDirectoryAsync(string sourceDirectory, string relative)
        {
            EnsureOrdinaryPath(sourceDirectory);
            if (!Directory.Exists(sourceDirectory)) return;
            Directory.CreateDirectory(Path.Combine(stage, relative));
            foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOrdinaryPath(entry);
                var destinationRelative = Path.Combine(relative, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    await CopyDirectoryAsync(entry, destinationRelative);
                    continue;
                }
                var destination = Path.Combine(stage, destinationRelative);
                byte[] hash;
                await using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output, cancellationToken);
                    input.Position = 0;
                    hash = await SHA256.HashDataAsync(input, cancellationToken);
                }
                await using (var output = File.OpenRead(destination))
                    if (!(await SHA256.HashDataAsync(output, cancellationToken)).AsSpan().SequenceEqual(hash))
                        throw new IOException("迁移校验失败。");
                files.Add((entry, destinationRelative, hash));
            }
        }
    }

    private void ValidateTarget(string target, bool allowExisting = false)
    {
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("请选择本地磁盘。");
        if (new DriveInfo(Path.GetPathRoot(target)!).DriveType == DriveType.Network)
            throw new IOException("请选择本地磁盘。");
        EnsureOrdinaryPath(target);
        foreach (var root in new[] { defaultData, defaultCache, DataPath, CachePath, configuration.Root })
            if (root is not null && (IsWithin(target, root) || IsWithin(root, target)))
                throw new IOException("新目录不能与当前或默认缓存目录重叠。");
        if ((!allowExisting && Directory.Exists(target)) || File.Exists(target))
            throw new IOException("所选位置已有 RichChatData，请选择其他文件夹，避免覆盖已有数据。");
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureOrdinaryPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缓存目录不能包含符号链接或目录联接。");
    }

    private async Task SaveAsync(Configuration value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(defaultData);
        var temporary = configurationPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken);
        File.Move(temporary, configurationPath, true);
    }

    private static void CollectFiles(string directory, HashSet<string> files)
    {
        EnsureOrdinaryPath(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureOrdinaryPath(entry);
            if (Directory.Exists(entry)) CollectFiles(entry, files);
            else files.Add(entry);
        }
    }

    private static async Task VerifyAsync(string path, byte[] expected)
    {
        await using var stream = File.OpenRead(path);
        if (!(await SHA256.HashDataAsync(stream)).AsSpan().SequenceEqual(expected))
            throw new IOException("迁移文件校验失败。");
    }

    private sealed record Configuration(string? Root, string? Pending, string? MigrationId);
    private sealed record Migration(string Id, string SourceData, string SourceCache, MigratedFile[] Files);
    private sealed record MigratedFile(string Relative, string Hash);
}
