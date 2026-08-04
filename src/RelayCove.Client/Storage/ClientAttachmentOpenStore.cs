using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RelayCove.Client.Storage;

// Owns only short-lived, policy-marked copies for Windows Attachment Manager. Cache paths,
// attachment IDs, hashes, and original names deliberately do not participate in its names.
internal sealed class ClientAttachmentOpenStore
{
    internal const long DefaultQuotaBytes = 1024L * 1024 * 1024;
    internal const int DefaultMaximumFileCount = 64;
    private const long MaximumAttachmentBytes = ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize;
    private const string ZoneIdentifierStreamName = ":Zone.Identifier";
    private const string ZoneIdentifierContents = "[ZoneTransfer]\r\nZoneId=4\r\n";
    private static readonly Regex ManagedFileName = new(
        "\\A[0-9a-f]{32}\\.[a-z0-9]{1,16}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TerminalExtension = new(
        "\\A[a-z0-9]{1,16}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, ScopeState> ProcessScopeStates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ScopeState scopeState;
    private readonly long quotaBytes;
    private readonly int maximumFileCount;

    internal ClientAttachmentOpenStore(AccountScopeIdentity identity)
        : this(identity, Path.Combine(Path.GetTempPath(), "RelayCove"), DefaultQuotaBytes, DefaultMaximumFileCount)
    {
    }

    internal ClientAttachmentOpenStore(
        AccountScopeIdentity identity,
        string rootDirectory,
        long quotaBytes = DefaultQuotaBytes,
        int maximumFileCount = DefaultMaximumFileCount)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The open-copy root must be absolute.", nameof(rootDirectory));
        }

        if (quotaBytes is <= 0 or > DefaultQuotaBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        }

        if (maximumFileCount is <= 0 or > DefaultMaximumFileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileCount));
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        ScopeDirectory = ResolveChildPath(RootDirectory, Identity.Id);
        scopeState = ProcessScopeStates.GetOrAdd(ScopeDirectory, static _ => new ScopeState());
        this.quotaBytes = quotaBytes;
        this.maximumFileCount = maximumFileCount;
    }

    internal AccountScopeIdentity Identity { get; }

    internal string RootDirectory { get; }

    internal string ScopeDirectory { get; }

    internal async Task<ClientAttachmentOpenCopyOutcome> CreateCopyAsync(
        ClientAttachmentCacheStore.ValidatedFile source,
        string originalFileName,
        long expectedSize,
        string expectedLowercaseSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryGetCanonicalExtension(originalFileName, out var extension))
        {
            return new ClientAttachmentOpenCopyOutcome(
                ClientAttachmentOpenStoreStatus.InvalidFileName,
                Lease: null);
        }

        ValidateExpectedContent(expectedSize, expectedLowercaseSha256);
        ClientAttachmentOpenLease? lease = null;
        try
        {
            var reservation = await ReserveAndCreateAsync(extension, expectedSize, cancellationToken)
                .ConfigureAwait(false);
            lease = reservation.Lease;
            if (lease is null)
            {
                return new ClientAttachmentOpenCopyOutcome(
                    reservation.Status,
                    Lease: null);
            }

            var copied = await CopyAndVerifyAsync(
                    source,
                    lease.LocalPath,
                    expectedSize,
                    expectedLowercaseSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!copied)
            {
                await DisposeLeaseAsync(lease).ConfigureAwait(false);
                return new ClientAttachmentOpenCopyOutcome(
                    ClientAttachmentOpenStoreStatus.ValidationFailed,
                    Lease: null);
            }

            await ReleaseReservationAsync(lease).ConfigureAwait(false);
            return new ClientAttachmentOpenCopyOutcome(ClientAttachmentOpenStoreStatus.Ready, lease);
        }
        catch (OperationCanceledException)
        {
            if (lease is not null)
            {
                await DisposeLeaseAsync(lease).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            if (lease is not null)
            {
                await DisposeLeaseAsync(lease).ConfigureAwait(false);
            }

            return new ClientAttachmentOpenCopyOutcome(
                ClientAttachmentOpenStoreStatus.StorageFailure,
                Lease: null);
        }
    }

    // The coordinator calls this from its final authorization/UI commit after all potentially
    // failing I/O has completed and the STA worker owns the job. It cannot wait or touch disk.
    internal void Commit(ClientAttachmentOpenLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsOwnedBy(this))
        {
            throw new ArgumentException("The open lease belongs to another store.", nameof(lease));
        }

        if (!lease.IsDisposed)
        {
            lease.TryMarkCommitted();
        }
    }

    // Must run after the STA Execute attempt has returned. A concurrent logout/revocation
    // only marks a committed, active job for purge; this is the first point at which deleting
    // its LocalPath cannot race Attachment Manager.
    internal async Task<ClientAttachmentOpenCleanupOutcome> CompleteLaunchAsync(
        ClientAttachmentOpenLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsOwnedBy(this))
        {
            throw new ArgumentException("The open lease belongs to another store.", nameof(lease));
        }

        if (!lease.TryMarkLaunchCompleted())
        {
            throw new InvalidOperationException("Only an active committed open lease can complete.");
        }

        return lease.IsPurgeRequested
            ? await CleanupCommittedAsync(cancellationToken).ConfigureAwait(false)
            : new ClientAttachmentOpenCleanupOutcome(
                ClientAttachmentOpenStoreStatus.Ready,
                DeletedCount: 0,
                PendingRetryCount: 0);
    }

    // Deletes committed copies and any prior failed pre-commit cleanups. Uncommitted leases
    // are active launch work and are intentionally skipped across runtime generations.
    internal async Task<ClientAttachmentOpenCleanupOutcome> CleanupCommittedAsync(
        CancellationToken cancellationToken = default)
    {
        await scopeState.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureScopeDirectory();
            var candidates = scopeState.PendingCleanupPaths
                .Concat(scopeState.Leases
                    .Where(static pair => pair.Key.IsCommitted && pair.Key.IsLaunchCompleted)
                    .Select(static pair => pair.Value.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var deleted = 0;
            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activePurgeCount = 0;
            foreach (var lease in scopeState.Leases.Keys.Where(static lease =>
                         lease.IsCommitted && !lease.IsLaunchCompleted))
            {
                lease.RequestPurge();
                activePurgeCount++;
            }
            foreach (var path in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryDeleteManagedFile(path))
                {
                    deleted++;
                }
                else if (File.Exists(path))
                {
                    pending.Add(path);
                }

                foreach (var lease in scopeState.Leases
                    .Where(pair => string.Equals(pair.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                    .Select(static pair => pair.Key)
                    .ToArray())
                {
                    scopeState.Leases.Remove(lease);
                    lease.MarkDisposedAfterCleanup();
                }
            }

            scopeState.PendingCleanupPaths.Clear();
            scopeState.PendingCleanupPaths.UnionWith(pending);
            return new ClientAttachmentOpenCleanupOutcome(
                pending.Count == 0 && activePurgeCount == 0
                    ? ClientAttachmentOpenStoreStatus.Ready
                    : ClientAttachmentOpenStoreStatus.CleanupPending,
                deleted,
                pending.Count + activePurgeCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentOpenCleanupOutcome(
                ClientAttachmentOpenStoreStatus.StorageFailure,
                DeletedCount: 0,
                scopeState.PendingCleanupPaths.Count);
        }
        finally
        {
            scopeState.Gate.Release();
        }
    }

    // Startup recovery removes only strictly-managed names. It never follows reparse points,
    // and a store sharing this process scope will not delete another generation's live lease.
    internal async Task<ClientAttachmentOpenRecoveryOutcome> RecoverOrphansAsync(
        CancellationToken cancellationToken = default)
    {
        await scopeState.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureScopeDirectory();
            var activePaths = scopeState.Leases.Values
                .Select(static entry => entry.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deleted = 0;
            var pending = new HashSet<string>(scopeState.PendingCleanupPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateManagedFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (activePaths.Contains(path))
                {
                    continue;
                }

                if (TryDeleteManagedFile(path))
                {
                    deleted++;
                    pending.Remove(path);
                }
                else if (File.Exists(path))
                {
                    pending.Add(path);
                }
            }

            scopeState.PendingCleanupPaths.Clear();
            scopeState.PendingCleanupPaths.UnionWith(pending);
            return new ClientAttachmentOpenRecoveryOutcome(
                pending.Count == 0
                    ? ClientAttachmentOpenStoreStatus.Ready
                    : ClientAttachmentOpenStoreStatus.CleanupPending,
                deleted,
                pending.Count,
                activePaths.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentOpenRecoveryOutcome(
                ClientAttachmentOpenStoreStatus.StorageFailure,
                DeletedCount: 0,
                scopeState.PendingCleanupPaths.Count,
                scopeState.Leases.Count);
        }
        finally
        {
            scopeState.Gate.Release();
        }
    }

    internal async Task DisposeLeaseAsync(ClientAttachmentOpenLease lease)
    {
        if (!lease.IsOwnedBy(this))
        {
            return;
        }

        if (!lease.TryDisposePrecommit())
        {
            return;
        }

        await scopeState.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!scopeState.Leases.Remove(lease, out var entry))
            {
                return;
            }

            scopeState.ReservedBytes -= entry.ReservedBytes;
            if (!TryDeleteManagedFile(entry.Path) && File.Exists(entry.Path))
            {
                scopeState.PendingCleanupPaths.Add(entry.Path);
            }
        }
        finally
        {
            scopeState.Gate.Release();
        }
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenStore)} {{ Identity = [REDACTED], RootDirectory = [REDACTED], " +
        "ScopeDirectory = [REDACTED], QuotaBytes = [REDACTED] }}";

    private async Task<ReservationOutcome> ReserveAndCreateAsync(
        string extension,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await scopeState.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScopeDirectory();
            var files = EnumerateManagedFiles();
            var reservedPaths = scopeState.Leases
                .Where(static pair => pair.Value.ReservedBytes != 0)
                .Select(static pair => pair.Value.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedBytes = files
                .Where(path => !reservedPaths.Contains(path))
                .Sum(static path => new FileInfo(path).Length);
            if (files.Count >= maximumFileCount)
            {
                return new ReservationOutcome(ClientAttachmentOpenStoreStatus.StoreFull, Lease: null);
            }

            if (usedBytes > quotaBytes - scopeState.ReservedBytes ||
                expectedSize > quotaBytes - usedBytes - scopeState.ReservedBytes)
            {
                return new ReservationOutcome(ClientAttachmentOpenStoreStatus.QuotaExceeded, Lease: null);
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var path = ResolveChildPath(
                    ScopeDirectory,
                    $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}.{extension}");
                try
                {
                    using var created = new FileStream(
                        path,
                        new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            Options = FileOptions.WriteThrough,
                        });
                    created.Flush(flushToDisk: true);
                    var lease = new ClientAttachmentOpenLease(this, path);
                    scopeState.Leases.Add(lease, new LeaseEntry(path, expectedSize));
                    scopeState.ReservedBytes = checked(scopeState.ReservedBytes + expectedSize);
                    return new ReservationOutcome(ClientAttachmentOpenStoreStatus.Ready, lease);
                }
                catch (IOException) when (attempt < 15)
                {
                    // 128-bit CreateNew collisions are harmless; persistent failures fail closed.
                }
            }

            throw new IOException("Unable to allocate a managed open-copy file.");
        }
        finally
        {
            scopeState.Gate.Release();
        }
    }

    private async Task ReleaseReservationAsync(ClientAttachmentOpenLease lease)
    {
        await scopeState.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!scopeState.Leases.TryGetValue(lease, out var entry))
            {
                throw new InvalidOperationException("The open lease was lost while copying.");
            }

            scopeState.ReservedBytes -= entry.ReservedBytes;
            entry.ReservedBytes = 0;
        }
        finally
        {
            scopeState.Gate.Release();
        }
    }

    private static async Task<bool> CopyAndVerifyAsync(
        ClientAttachmentCacheStore.ValidatedFile source,
        string path,
        long expectedSize,
        string expectedLowercaseSha256,
        CancellationToken cancellationToken)
    {
        var copied = await source.ReadContentAsync(
                async (content, readCancellationToken) =>
                {
                    RejectReparsePoint(path);
                    await using var destination = new FileStream(
                        path,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = 81920,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                        });
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = ArrayPool<byte>.Shared.Rent(81920);
                    try
                    {
                        long length = 0;
                        while (true)
                        {
                            var read = await content.ReadAsync(buffer.AsMemory(), readCancellationToken)
                                .ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            length = checked(length + read);
                            if (length > expectedSize)
                            {
                                return false;
                            }

                            hash.AppendData(buffer, 0, read);
                            await destination.WriteAsync(buffer.AsMemory(0, read), readCancellationToken)
                                .ConfigureAwait(false);
                        }

                        await destination.FlushAsync(readCancellationToken).ConfigureAwait(false);
                        destination.Flush(flushToDisk: true);
                        return length == expectedSize &&
                            string.Equals(
                                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                                expectedLowercaseSha256,
                                StringComparison.Ordinal);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!copied)
        {
            return false;
        }

        RejectReparsePoint(path);
        await File.WriteAllTextAsync(
                path + ZoneIdentifierStreamName,
                ZoneIdentifierContents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        var zoneIdentifier = await File.ReadAllTextAsync(
                path + ZoneIdentifierStreamName,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(zoneIdentifier, ZoneIdentifierContents, StringComparison.Ordinal))
        {
            return false;
        }

        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize)
        {
            return false;
        }

        await using var verification = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var verifiedHash = await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(verifiedHash).ToLowerInvariant(),
            expectedLowercaseSha256,
            StringComparison.Ordinal);
    }

    private static bool TryGetCanonicalExtension(string? originalFileName, out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(originalFileName) ||
            !string.Equals(originalFileName, originalFileName.TrimEnd(' ', '.'), StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(originalFileName) ||
            !string.Equals(originalFileName, Path.GetFileName(originalFileName), StringComparison.Ordinal) ||
            originalFileName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            !IsValidWindowsLeafText(originalFileName))
        {
            return false;
        }

        var rawExtension = Path.GetExtension(originalFileName);
        if (rawExtension.Length < 2 || rawExtension[0] != '.')
        {
            return false;
        }

        var stem = originalFileName[..^rawExtension.Length];
        if (string.IsNullOrEmpty(stem) || stem[0] == '.' || IsReservedWindowsDeviceStem(stem))
        {
            return false;
        }

        extension = rawExtension[1..].ToLowerInvariant();
        return TerminalExtension.IsMatch(extension);
    }

    private static bool IsValidWindowsLeafText(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool IsReservedWindowsDeviceStem(string stem)
    {
        if (string.Equals(stem, "CON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "PRN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "AUX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "NUL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "CLOCK$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    private List<string> EnumerateManagedFiles()
    {
        if (Directory.EnumerateDirectories(ScopeDirectory, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException("Open-copy storage contains an unexpected directory.");
        }

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(ScopeDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!ManagedFileName.IsMatch(name))
            {
                throw new InvalidDataException("Open-copy storage contains an unmanaged file.");
            }

            RejectReparsePoint(path);
            files.Add(path);
        }

        return files;
    }

    private bool TryDeleteManagedFile(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        if (!string.Equals(Path.GetDirectoryName(path), ScopeDirectory, StringComparison.OrdinalIgnoreCase) ||
            !ManagedFileName.IsMatch(Path.GetFileName(path)))
        {
            throw new InvalidDataException("The open-copy cleanup path is not managed.");
        }

        RejectReparsePoint(path);
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return false;
        }
    }

    private void EnsureScopeDirectory()
    {
        EnsureSafeDirectory(RootDirectory);
        EnsureSafeDirectory(ScopeDirectory);
    }

    private static void EnsureSafeDirectory(string path)
    {
        EnsureNoReparseComponents(path);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        EnsureNoReparseComponents(path);
        RejectReparsePoint(path);
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists)
            {
                RejectReparsePoint(current.FullName);
            }

            var parent = current.Parent;
            if (parent is null || string.Equals(parent.FullName, current.FullName, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Open-copy storage contains a reparse point.");
        }
    }

    private static string ResolveChildPath(string root, string child)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullChild = Path.GetFullPath(Path.Combine(fullRoot, child));
        var relative = Path.GetRelativePath(fullRoot, fullChild);
        if (Path.IsPathFullyQualified(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The open-copy path escaped its root.");
        }

        return fullChild;
    }

    private static void ValidateExpectedContent(long expectedSize, string expectedLowercaseSha256)
    {
        if (expectedSize is < 1 or > MaximumAttachmentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }

        if (expectedLowercaseSha256 is null ||
            expectedLowercaseSha256.Length != 64 ||
            expectedLowercaseSha256.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 must be lowercase hexadecimal.", nameof(expectedLowercaseSha256));
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
        System.Security.SecurityException;

    private sealed class ScopeState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Dictionary<ClientAttachmentOpenLease, LeaseEntry> Leases { get; } = new();

        public HashSet<string> PendingCleanupPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long ReservedBytes;

    }

    private sealed class LeaseEntry(string path, long reservedBytes)
    {
        public string Path { get; } = path;

        public long ReservedBytes { get; set; } = reservedBytes;

    }

    private sealed record ReservationOutcome(
        ClientAttachmentOpenStoreStatus Status,
        ClientAttachmentOpenLease? Lease);
}
