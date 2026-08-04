using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RelayCove.Client.Storage;

internal sealed class ClientAttachmentCacheStore : IClientAttachmentCacheStore
{
    internal const long DefaultQuotaBytes = 1024L * 1024 * 1024;
    private const long MaximumAttachmentBytes = 100L * 1024 * 1024;
    private static readonly Regex FinalName = new(
        "\\A(?<conversation>[0-9a-f]{32})\\.(?<attachment>[0-9a-f]{32})\\.(?<hash>[0-9a-f]{64})\\.cache\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StagingName = new(
        "\\A(?<conversation>[0-9a-f]{32})\\.(?<attachment>[0-9a-f]{32})\\.(?<random>[0-9a-f]{32})\\.part\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, ScopeStoreState> ProcessScopeStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ScopeStoreState scopeState;
    private readonly SemaphoreSlim operationGate;
    private readonly long quotaBytes;
    private static readonly object ValidatedFileToken = new();

    internal ClientAttachmentCacheStore(
        AccountScopeIdentity identity,
        string cacheRoot)
        : this(identity, cacheRoot, DefaultQuotaBytes)
    {
    }

    internal ClientAttachmentCacheStore(
        AccountScopeIdentity identity,
        string cacheRoot,
        long quotaBytes)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (!Path.IsPathFullyQualified(cacheRoot))
        {
            throw new ArgumentException("The cache root must be an absolute path.", nameof(cacheRoot));
        }

        if (quotaBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        }

        CacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        ScopeDirectory = ResolveChildPath(CacheRoot, Identity.Id);
        scopeState = ProcessScopeStates.GetOrAdd(
            ScopeDirectory,
            static _ => new ScopeStoreState());
        operationGate = scopeState.OperationGate;
        this.quotaBytes = quotaBytes;
    }

    internal AccountScopeIdentity Identity { get; }

    internal string CacheRoot { get; }

    internal string ScopeDirectory { get; }

    internal sealed class ValidatedFile : IDisposable
    {
        private readonly FileStream stream;
        private readonly string fullPath;
        private int disposed;

        internal ValidatedFile(
            string fullPath,
            FileStream stream,
            object validationToken)
        {
            if (!ReferenceEquals(validationToken, ValidatedFileToken))
            {
                throw new InvalidOperationException(
                    "Validated cache files can only be created by their owning store.");
            }

            this.fullPath = fullPath;
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        internal string FullPath
        {
            get
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref disposed) != 0,
                    this);
                return fullPath;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                stream.Dispose();
            }
        }

        public override string ToString() =>
            $"{nameof(ValidatedFile)} {{ FullPath = [REDACTED] }}";
    }

    public async Task<ClientAttachmentCacheStoreStagingOutcome> CreateStagingAsync(
        Guid conversationId,
        Guid attachmentId,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentityIds(conversationId, attachmentId);
        ValidateExpectedSize(expectedSize);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScopeDirectory();
            var usedBytes = GetManagedFinalBytes();
            if (usedBytes > quotaBytes - scopeState.ReservedBytes ||
                expectedSize > quotaBytes - usedBytes - scopeState.ReservedBytes)
            {
                return new ClientAttachmentCacheStoreStagingOutcome(
                    ClientAttachmentCacheStoreStatus.QuotaExceeded,
                    StagingFile: null);
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var stagingPath = ResolveChildPath(
                    ScopeDirectory,
                    $"{conversationId:N}.{attachmentId:N}.{Guid.NewGuid():N}.part");
                try
                {
                    var stream = new FileStream(
                        stagingPath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = 81920,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                        });
                    var stagingFile = new ClientAttachmentCacheStoreStagingFile(
                        this,
                        conversationId,
                        attachmentId,
                        expectedSize,
                        stagingPath,
                        stream);
                    scopeState.Reservations.Add(stagingFile, expectedSize);
                    scopeState.ReservedBytes = checked(
                        scopeState.ReservedBytes + expectedSize);
                    return new ClientAttachmentCacheStoreStagingOutcome(
                        ClientAttachmentCacheStoreStatus.Ready,
                        stagingFile);
                }
                catch (IOException) when (attempt < 15)
                {
                    // A random CreateNew collision is harmless; any persistent I/O failure is reported below.
                }
            }

            return new ClientAttachmentCacheStoreStagingOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                StagingFile: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreStagingOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                StagingFile: null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStorePublishOutcome> PublishAsync(
        ClientAttachmentCacheStoreStagingFile stagingFile,
        string verifiedLowercaseSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagingFile);
        if (!stagingFile.IsOwnedBy(this))
        {
            throw new ArgumentException("The staging file belongs to another cache store.", nameof(stagingFile));
        }

        var key = new ClientAttachmentCacheStoreKey(
            stagingFile.ConversationId,
            stagingFile.AttachmentId,
            verifiedLowercaseSha256);

        string? stagingPath = null;
        try
        {
            stagingPath = stagingFile.TakePathForPublish();
            await stagingFile.FlushAndCloseAsync(cancellationToken).ConfigureAwait(false);
            if (!await IsMatchingFileAsync(
                    stagingPath,
                    key.Sha256,
                    stagingFile.ExpectedSize,
                    cancellationToken).ConfigureAwait(false))
            {
                await DiscardCompletedAsync(stagingFile, stagingPath).ConfigureAwait(false);
                return new ClientAttachmentCacheStorePublishOutcome(
                    ClientAttachmentCacheStoreStatus.ValidationFailed,
                    RelativePath: null);
            }
        }
        catch (OperationCanceledException)
        {
            await DiscardCompletedAsync(stagingFile, stagingPath).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            await DiscardCompletedAsync(stagingFile, stagingPath).ConfigureAwait(false);
            return new ClientAttachmentCacheStorePublishOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                RelativePath: null);
        }

        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DiscardCompletedAsync(stagingFile, stagingPath).ConfigureAwait(false);
            throw;
        }
        try
        {
            EnsureScopeDirectory();
            var relativePath = GetFinalRelativePath(key);
            var finalPath = ResolveFinalPath(relativePath, key);
            var usedBytes = GetManagedFinalBytes();
            var reservation = GetReservation(stagingFile);
            if (usedBytes > quotaBytes - (scopeState.ReservedBytes - reservation) ||
                stagingFile.ExpectedSize >
                    quotaBytes - usedBytes - (scopeState.ReservedBytes - reservation))
            {
                TryDeleteManagedPath(stagingPath);
                ReleaseReservation(stagingFile);
                return new ClientAttachmentCacheStorePublishOutcome(
                    ClientAttachmentCacheStoreStatus.QuotaExceeded,
                    RelativePath: null);
            }

            try
            {
                File.Move(stagingPath!, finalPath, overwrite: false);
                ReleaseReservation(stagingFile);
                return new ClientAttachmentCacheStorePublishOutcome(
                    ClientAttachmentCacheStoreStatus.Ready,
                    relativePath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                RejectReparsePoint(finalPath);
                var existingIsValid = await IsMatchingFileAsync(
                    finalPath,
                    key.Sha256,
                    stagingFile.ExpectedSize,
                    cancellationToken).ConfigureAwait(false);
                if (existingIsValid)
                {
                    TryDeleteManagedPath(stagingPath);
                    ReleaseReservation(stagingFile);
                    return new ClientAttachmentCacheStorePublishOutcome(
                        ClientAttachmentCacheStoreStatus.AlreadyPublished,
                        relativePath);
                }

                TryDeleteManagedPath(finalPath);
                File.Move(stagingPath!, finalPath, overwrite: false);
                ReleaseReservation(stagingFile);
                return new ClientAttachmentCacheStorePublishOutcome(
                    ClientAttachmentCacheStoreStatus.Ready,
                    relativePath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            TryDeleteManagedPath(stagingPath);
            ReleaseReservation(stagingFile);
            return new ClientAttachmentCacheStorePublishOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                RelativePath: null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStoreValidationOutcome> ValidateAsync(
        string relativePath,
        ClientAttachmentCacheStoreKey expectedKey,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ValidateAndResolveAsync(
                relativePath,
                expectedKey,
                expectedSize,
                cancellationToken)
            .ConfigureAwait(false);
        using var file = outcome.File;
        return new ClientAttachmentCacheStoreValidationOutcome(
            outcome.Status,
            outcome.Status == ClientAttachmentCacheStoreStatus.Ready && file is not null);
    }

    public async Task<ClientAttachmentCacheStoreResolutionOutcome> ValidateAndResolveAsync(
        string relativePath,
        ClientAttachmentCacheStoreKey expectedKey,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedKey);
        ValidateExpectedSize(expectedSize);
        if (!TryParseFinalRelativePath(relativePath, out var actualKey) ||
            !KeysEqual(actualKey!, expectedKey))
        {
            return new ClientAttachmentCacheStoreResolutionOutcome(
                ClientAttachmentCacheStoreStatus.InvalidRelativePath,
                File: null);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScopeDirectory();
            var path = ResolveFinalPath(relativePath, expectedKey);
            if (!File.Exists(path))
            {
                return new ClientAttachmentCacheStoreResolutionOutcome(
                    ClientAttachmentCacheStoreStatus.NotFound,
                    File: null);
            }

            var file = await OpenValidatedFileAsync(
                    path,
                    expectedKey.Sha256,
                    expectedSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (file is null)
            {
                return new ClientAttachmentCacheStoreResolutionOutcome(
                    ClientAttachmentCacheStoreStatus.ValidationFailed,
                    File: null);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureScopeDirectory();
                var finalPath = ResolveFinalPath(relativePath, expectedKey);
                if (!string.Equals(path, finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    file.Dispose();
                    return new ClientAttachmentCacheStoreResolutionOutcome(
                        ClientAttachmentCacheStoreStatus.ValidationFailed,
                        File: null);
                }

                RejectReparsePoint(finalPath);
                return new ClientAttachmentCacheStoreResolutionOutcome(
                    ClientAttachmentCacheStoreStatus.Ready,
                    file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreResolutionOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                File: null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStoreEnumerationOutcome> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureScopeDirectory();
            return new ClientAttachmentCacheStoreEnumerationOutcome(
                ClientAttachmentCacheStoreStatus.Ready,
                EnumerateManagedEntries(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreEnumerationOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                Array.Empty<ClientAttachmentCacheStoreEntry>());
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseManagedRelativePath(relativePath, out _))
        {
            return new ClientAttachmentCacheStoreDeleteOutcome(
                ClientAttachmentCacheStoreStatus.InvalidRelativePath,
                DeletedCount: 0);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScopeDirectory();
            var path = ResolveChildPath(ScopeDirectory, relativePath);
            if (!File.Exists(path))
            {
                return new ClientAttachmentCacheStoreDeleteOutcome(
                    ClientAttachmentCacheStoreStatus.NotFound,
                    DeletedCount: 0);
            }

            RejectReparsePoint(path);
            File.Delete(path);
            return new ClientAttachmentCacheStoreDeleteOutcome(
                ClientAttachmentCacheStoreStatus.Ready,
                DeletedCount: 1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreDeleteOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                DeletedCount: 0);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureScopeDirectory();
            var deletedCount = 0;
            var deletionFailed = false;
            foreach (var entry in EnumerateManagedEntries(cancellationToken))
            {
                if (entry.Key.ConversationId != conversationId)
                {
                    continue;
                }

                try
                {
                    var path = ResolveChildPath(ScopeDirectory, entry.RelativePath);
                    RejectReparsePoint(path);
                    File.Delete(path);
                    deletedCount++;
                }
                catch (Exception exception) when (IsStorageException(exception))
                {
                    // An active .part may still be held with FileShare.None while revocation
                    // cancellation unwinds. Continue so later managed finals are still purged;
                    // the coordinator retries once all flights for this conversation quiesce.
                    deletionFailed = true;
                }
            }

            return new ClientAttachmentCacheStoreDeleteOutcome(
                deletionFailed
                    ? ClientAttachmentCacheStoreStatus.StorageFailure
                    : ClientAttachmentCacheStoreStatus.Ready,
                deletedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreDeleteOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                DeletedCount: 0);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ClientAttachmentCacheStoreQuotaOutcome> GetQuotaAsync(
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureScopeDirectory();
            return new ClientAttachmentCacheStoreQuotaOutcome(
                ClientAttachmentCacheStoreStatus.Ready,
                GetManagedFinalBytes(),
                quotaBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return new ClientAttachmentCacheStoreQuotaOutcome(
                ClientAttachmentCacheStoreStatus.StorageFailure,
                UsedBytes: 0,
                quotaBytes);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task DiscardAsync(ClientAttachmentCacheStoreStagingFile stagingFile)
    {
        if (!stagingFile.IsOwnedBy(this))
        {
            return;
        }

        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!scopeState.Reservations.ContainsKey(stagingFile))
            {
                return;
            }

            var path = await stagingFile.TakePathForDiscardAsync().ConfigureAwait(false);
            TryDeleteManagedPath(path);
            ReleaseReservation(stagingFile);
        }
        catch (InvalidOperationException)
        {
            ReleaseReservation(stagingFile);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStore)} {{ Identity = [REDACTED], " +
        "CacheRoot = [REDACTED], ScopeDirectory = [REDACTED], QuotaBytes = [REDACTED] }";

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException;

    private static void ValidateExpectedSize(long expectedSize)
    {
        if (expectedSize is < 1 or > MaximumAttachmentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }
    }

    private static void ValidateIdentityIds(Guid conversationId, Guid attachmentId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (attachmentId == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", nameof(attachmentId));
        }
    }

    private static bool KeysEqual(
        ClientAttachmentCacheStoreKey first,
        ClientAttachmentCacheStoreKey second) =>
        first.ConversationId == second.ConversationId &&
        first.AttachmentId == second.AttachmentId &&
        string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal);

    private string GetFinalRelativePath(ClientAttachmentCacheStoreKey key) =>
        $"{key.ConversationId:N}.{key.AttachmentId:N}.{key.Sha256}.cache";

    private string ResolveFinalPath(string relativePath, ClientAttachmentCacheStoreKey key)
    {
        if (!TryParseFinalRelativePath(relativePath, out var parsed) || !KeysEqual(parsed!, key))
        {
            throw new InvalidDataException("The cache file name is not managed by this store.");
        }

        return ResolveChildPath(ScopeDirectory, relativePath);
    }

    private static bool TryParseManagedRelativePath(
        string? relativePath,
        out ClientAttachmentCacheStoreKey? key) =>
        TryParseFinalRelativePath(relativePath, out key) ||
        TryParseStagingRelativePath(relativePath, out key);

    private static bool TryParseFinalRelativePath(
        string? relativePath,
        out ClientAttachmentCacheStoreKey? key) =>
        TryParseRelativePath(relativePath, FinalName, includesHash: true, out key);

    private static bool TryParseStagingRelativePath(
        string? relativePath,
        out ClientAttachmentCacheStoreKey? key) =>
        TryParseRelativePath(relativePath, StagingName, includesHash: false, out key);

    private static bool TryParseRelativePath(
        string? relativePath,
        Regex pattern,
        bool includesHash,
        out ClientAttachmentCacheStoreKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath) ||
            !string.Equals(relativePath, Path.GetFileName(relativePath), StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Contains('/', StringComparison.Ordinal) ||
            relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var match = pattern.Match(relativePath);
        if (!match.Success ||
            !Guid.TryParseExact(match.Groups["conversation"].Value, "N", out var conversationId) ||
            !Guid.TryParseExact(match.Groups["attachment"].Value, "N", out var attachmentId))
        {
            return false;
        }

        key = new ClientAttachmentCacheStoreKey(
            conversationId,
            attachmentId,
            includesHash ? match.Groups["hash"].Value : new string('0', 64));
        return true;
    }

    private List<ClientAttachmentCacheStoreEntry> EnumerateManagedEntries(
        CancellationToken cancellationToken)
    {
        var entries = new List<ClientAttachmentCacheStoreEntry>();
        foreach (var path in Directory.EnumerateFiles(ScopeDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetFileName(path);
            if (!TryParseFinalRelativePath(relativePath, out var finalKey) &&
                !TryParseStagingRelativePath(relativePath, out finalKey))
            {
                continue;
            }

            RejectReparsePoint(path);
            var kind = FinalName.IsMatch(relativePath)
                ? ClientAttachmentCacheStoreEntryKind.Final
                : ClientAttachmentCacheStoreEntryKind.Staging;
            entries.Add(new ClientAttachmentCacheStoreEntry(
                kind,
                finalKey!,
                relativePath,
                new FileInfo(path).Length));
        }

        return entries;
    }

    private long GetManagedFinalBytes()
    {
        long usedBytes = 0;
        foreach (var entry in EnumerateManagedEntries(CancellationToken.None))
        {
            if (entry.Kind != ClientAttachmentCacheStoreEntryKind.Final)
            {
                continue;
            }

            usedBytes = checked(usedBytes + entry.Length);
        }

        return usedBytes;
    }

    private long GetReservation(ClientAttachmentCacheStoreStagingFile stagingFile)
    {
        if (!scopeState.Reservations.TryGetValue(stagingFile, out var reservation))
        {
            throw new InvalidDataException("The staging file is no longer owned by this cache store.");
        }

        return reservation;
    }

    private void ReleaseReservation(ClientAttachmentCacheStoreStagingFile stagingFile)
    {
        if (scopeState.Reservations.Remove(stagingFile, out var reservation))
        {
            scopeState.ReservedBytes -= reservation;
        }
    }

    private static async Task<bool> IsMatchingFileAsync(
        string path,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != expectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash).ToLowerInvariant(),
            expectedSha256,
            StringComparison.Ordinal);
    }

    private static async Task<ValidatedFile?> OpenValidatedFileAsync(
        string path,
        string expectedSha256,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 81920,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            if (stream.Length != expectedSize)
            {
                return null;
            }

            var hash = await SHA256
                .HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToHexString(hash).ToLowerInvariant(),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(path);
            var finalInfo = new FileInfo(path);
            if (!finalInfo.Exists || finalInfo.Length != expectedSize)
            {
                return null;
            }

            var validated = new ValidatedFile(path, stream, ValidatedFileToken);
            stream = null;
            return validated;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task DiscardCompletedAsync(
        ClientAttachmentCacheStoreStagingFile stagingFile,
        string? stagingPath)
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TryDeleteManagedPath(stagingPath);
            ReleaseReservation(stagingFile);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void EnsureScopeDirectory()
    {
        EnsureSafeDirectory(CacheRoot);
        EnsureSafeDirectory(ScopeDirectory);
    }

    private static void EnsureSafeDirectory(string path)
    {
        EnsureNoReparseComponents(path);
        if (Directory.Exists(path))
        {
            RejectReparsePoint(path);
            return;
        }

        Directory.CreateDirectory(path);
        EnsureNoReparseComponents(path);
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
            throw new InvalidDataException("Cache storage contains a reparse point.");
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
            throw new InvalidDataException("The cache storage path escaped its root.");
        }

        return fullChild;
    }

    private static void TryDeleteManagedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        RejectReparsePoint(path);
        File.Delete(path);
    }

    private sealed class ScopeStoreState
    {
        public SemaphoreSlim OperationGate { get; } = new(1, 1);

        public Dictionary<ClientAttachmentCacheStoreStagingFile, long> Reservations { get; } = new();

        public long ReservedBytes;
    }
}
