using System.IO;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed partial class AccountScopedLocalCache
{
    private const int DownloadingAttachmentStatus = 1;
    private const int DownloadedAttachmentStatus = 2;
    private const int FailedAttachmentStatus = 3;
    private Action<Guid>? attachmentDownloadCancellationRequested;
    private Func<Guid, Task>? attachmentCachePurged;

    internal event Action<Guid> AttachmentDownloadCancellationRequested
    {
        add
        {
            lock (scopeState.AttachmentEventGate)
            {
                scopeState.AttachmentDownloadCancellationRequested += value;
                attachmentDownloadCancellationRequested += value;
            }
        }
        remove
        {
            lock (scopeState.AttachmentEventGate)
            {
                scopeState.AttachmentDownloadCancellationRequested -= value;
                attachmentDownloadCancellationRequested -= value;
            }
        }
    }

    internal event Func<Guid, Task> AttachmentCachePurged
    {
        add
        {
            lock (scopeState.AttachmentEventGate)
            {
                scopeState.AttachmentCachePurged += value;
                attachmentCachePurged += value;
            }
        }
        remove
        {
            lock (scopeState.AttachmentEventGate)
            {
                scopeState.AttachmentCachePurged -= value;
                attachmentCachePurged -= value;
            }
        }
    }

    internal async Task<LocalAttachmentDownloadClaimOutcome> ClaimAttachmentDownloadAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalAttachmentDownloadClaimOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalAttachmentDownloadClaimOutcome outcome;
        try
        {
            outcome = await Task.Run(() => ClaimAttachmentDownload(conversationId, attachmentId))
                .ConfigureAwait(false);
            if (outcome.Status == LocalCacheOperationStatus.Ready &&
                outcome.Result == LocalAttachmentDownloadClaimResult.Claimed &&
                !scopeState.ActiveAttachmentDownloads.TryAdd(
                    (conversationId, attachmentId),
                    cacheInstanceId))
            {
                MarkScopeFatal();
                outcome = LocalAttachmentDownloadClaimOutcome.Failure(
                    LocalCacheOperationStatus.FatalScope);
            }
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.Result == LocalAttachmentDownloadClaimResult.Claimed)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalDownloadedAttachmentReadOutcome> ReadDownloadedAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ThrowIfDisposed();
        var initialStatus = GetConversationAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalDownloadedAttachmentReadOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => ReadDownloadedAttachment(conversationId, attachmentId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalDownloadedAttachmentConfirmationOutcome>
        ConfirmDownloadedAttachmentAsync(
            LocalAttachmentDownloadRecord expectedRecord,
            Action authorizeAction,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        ArgumentNullException.ThrowIfNull(authorizeAction);
        ValidateGuid(expectedRecord.ConversationId, nameof(expectedRecord));
        ValidateGuid(expectedRecord.Attachment.Id, nameof(expectedRecord));
        if (expectedRecord.State != LocalAttachmentDownloadState.Downloaded ||
            expectedRecord.LocalPath is null ||
            !ClientAttachmentMetadataPolicy.IsValid(expectedRecord.Attachment))
        {
            throw new ArgumentException(
                "A valid downloaded attachment record is required.",
                nameof(expectedRecord));
        }

        ValidateManagedAttachmentPath(
            expectedRecord.LocalPath,
            expectedRecord.ConversationId,
            expectedRecord.Attachment.Id);
        ThrowIfDisposed();
        var initialStatus = GetConversationAccessStatus(expectedRecord.ConversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalDownloadedAttachmentConfirmationOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => ConfirmDownloadedAttachment(
                        expectedRecord,
                        authorizeAction,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalCacheOperationStatus> CompleteAttachmentDownloadAsync(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ValidateManagedAttachmentPath(managedRelativePath, conversationId, attachmentId);
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            ReleaseAttachmentDownload(conversationId, attachmentId);
            return initialStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => CompleteAttachmentDownload(
                    conversationId,
                    attachmentId,
                    managedRelativePath))
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseAttachmentDownload(conversationId, attachmentId);
            operationGate.Release();
        }

        if (outcome == LocalCacheOperationStatus.Ready)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalCacheOperationStatus> FailAttachmentDownloadAsync(
        Guid conversationId,
        Guid attachmentId,
        bool canceled,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            ReleaseAttachmentDownload(conversationId, attachmentId);
            return initialStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => FailAttachmentDownload(
                    conversationId,
                    attachmentId,
                    canceled))
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseAttachmentDownload(conversationId, attachmentId);
            operationGate.Release();
        }

        if (outcome == LocalCacheOperationStatus.Ready)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalCacheOperationStatus> InvalidateDownloadedAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ValidateManagedAttachmentPath(managedRelativePath, conversationId, attachmentId);
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return initialStatus;
        }

        return await InvalidateDownloadedAttachmentCoreAsync(
                conversationId,
                attachmentId,
                managedRelativePath,
                requireAuthorizedAccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<LocalAttachmentCacheRecoveryOutcome> PrepareAttachmentCacheRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsFatal)
        {
            return LocalAttachmentCacheRecoveryOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(PrepareAttachmentCacheRecovery).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task<LocalCacheOperationStatus> InvalidateRecoveredAttachmentAsync(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ValidateManagedAttachmentPath(managedRelativePath, conversationId, attachmentId);
        ThrowIfDisposed();
        return InvalidateDownloadedAttachmentCoreAsync(
            conversationId,
            attachmentId,
            managedRelativePath,
            requireAuthorizedAccess: false,
            cancellationToken);
    }

    private LocalAttachmentDownloadClaimOutcome ClaimAttachmentDownload(
        Guid conversationId,
        Guid attachmentId)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                var databaseStatus = GetDatabaseAccessStatus(
                    connection,
                    transaction,
                    conversationId);
                if (databaseStatus != LocalCacheOperationStatus.Ready)
                {
                    return TransactionResult<LocalAttachmentDownloadClaimOutcome>.Rollback(
                        LocalAttachmentDownloadClaimOutcome.Failure(databaseStatus));
                }

                var record = ReadAttachmentDownloadRecord(
                    connection,
                    transaction,
                    conversationId,
                    attachmentId);
                if (record is null)
                {
                    return TransactionResult<LocalAttachmentDownloadClaimOutcome>.Rollback(
                        new LocalAttachmentDownloadClaimOutcome(
                            LocalCacheOperationStatus.Ready,
                            LocalAttachmentDownloadClaimResult.AttachmentUnavailable,
                            Record: null));
                }

                if (record.State == LocalAttachmentDownloadState.Downloading)
                {
                    return TransactionResult<LocalAttachmentDownloadClaimOutcome>.Rollback(
                        new LocalAttachmentDownloadClaimOutcome(
                            LocalCacheOperationStatus.Ready,
                            LocalAttachmentDownloadClaimResult.InProgress,
                            record));
                }

                if (record.State == LocalAttachmentDownloadState.Downloaded)
                {
                    return TransactionResult<LocalAttachmentDownloadClaimOutcome>.Rollback(
                        new LocalAttachmentDownloadClaimOutcome(
                            LocalCacheOperationStatus.Ready,
                            LocalAttachmentDownloadClaimResult.AlreadyDownloaded,
                            record));
                }

                using var update = CreateCommand(connection, transaction, """
                    UPDATE LocalAttachments
                    SET DownloadStatus = $downloading,
                        LocalPath = NULL,
                        ThumbnailLocalPath = NULL
                    WHERE Id = $attachmentId
                      AND DownloadStatus = $expectedStatus
                      AND EXISTS (
                          SELECT 1
                          FROM LocalMessages AS message
                          WHERE message.LocalId = LocalAttachments.LocalMessageId
                            AND message.ConversationId = $conversationId
                            AND message.ServerMessageId IS NOT NULL);
                    """);
                AddParameter(update, "$downloading", DownloadingAttachmentStatus);
                AddParameter(update, "$attachmentId", FormatGuid(attachmentId));
                AddParameter(update, "$expectedStatus", (int)record.State);
                AddParameter(update, "$conversationId", FormatGuid(conversationId));
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        "Claiming an attachment download did not update exactly one row.");
                }

                var claimedRecord = record with
                {
                    State = LocalAttachmentDownloadState.Downloading,
                    LocalPath = null,
                };
                return TransactionResult<LocalAttachmentDownloadClaimOutcome>.Commit(
                    new LocalAttachmentDownloadClaimOutcome(
                        LocalCacheOperationStatus.Ready,
                        LocalAttachmentDownloadClaimResult.Claimed,
                        claimedRecord));
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Claiming an attachment download remained busy after bounded retries; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalAttachmentDownloadClaimOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while claiming an " +
                "attachment download after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalAttachmentDownloadClaimOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalDownloadedAttachmentReadOutcome ReadDownloadedAttachment(
        Guid conversationId,
        Guid attachmentId)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            var databaseStatus = GetDatabaseAccessStatus(
                connection,
                transaction,
                conversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                transaction.Rollback();
                return LocalDownloadedAttachmentReadOutcome.Failure(databaseStatus);
            }

            var record = ReadAttachmentDownloadRecord(
                connection,
                transaction,
                conversationId,
                attachmentId);
            transaction.Rollback();
            if (record is null)
            {
                return new LocalDownloadedAttachmentReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    LocalDownloadedAttachmentReadResult.AttachmentUnavailable,
                    Record: null);
            }

            return record.State == LocalAttachmentDownloadState.Downloaded
                ? new LocalDownloadedAttachmentReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    LocalDownloadedAttachmentReadResult.Downloaded,
                    record)
                : new LocalDownloadedAttachmentReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    LocalDownloadedAttachmentReadResult.NotDownloaded,
                    Record: null);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Reading a downloaded attachment remained busy; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalDownloadedAttachmentReadOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while reading a " +
                "downloaded attachment after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalDownloadedAttachmentReadOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalDownloadedAttachmentConfirmationOutcome ConfirmDownloadedAttachment(
        LocalAttachmentDownloadRecord expectedRecord,
        Action authorizeAction,
        CancellationToken cancellationToken)
    {
        try
        {
            using var connection = OpenConnection();
            // The authorization action is the local-reveal linearization point. An
            // immediate transaction prevents a concurrent metadata/revocation writer
            // from committing between the final record comparison and that short
            // authorization transition. It must never invoke external code such as
            // the Windows Shell while this transaction is open.
            using var transaction = connection.BeginTransaction(deferred: false);
            var databaseStatus = GetDatabaseAccessStatus(
                connection,
                transaction,
                expectedRecord.ConversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                transaction.Rollback();
                return LocalDownloadedAttachmentConfirmationOutcome.Failure(databaseStatus);
            }

            var record = ReadAttachmentDownloadRecord(
                connection,
                transaction,
                expectedRecord.ConversationId,
                expectedRecord.Attachment.Id);
            var result = record switch
            {
                null => LocalDownloadedAttachmentConfirmationResult.AttachmentUnavailable,
                { State: not LocalAttachmentDownloadState.Downloaded } =>
                    LocalDownloadedAttachmentConfirmationResult.NotDownloaded,
                _ when record != expectedRecord =>
                    LocalDownloadedAttachmentConfirmationResult.Changed,
                _ => LocalDownloadedAttachmentConfirmationResult.Confirmed,
            };
            if (result == LocalDownloadedAttachmentConfirmationResult.Confirmed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    authorizeAction();
                }
                catch (Exception exception)
                {
                    throw new LocalDownloadedAttachmentAccessActionException(exception);
                }
            }

            transaction.Rollback();
            return new LocalDownloadedAttachmentConfirmationOutcome(
                LocalCacheOperationStatus.Ready,
                result);
        }
        catch (LocalDownloadedAttachmentAccessActionException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Confirming downloaded attachment access remained busy; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalDownloadedAttachmentConfirmationOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while confirming " +
                "downloaded attachment access after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalDownloadedAttachmentConfirmationOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalCacheOperationStatus CompleteAttachmentDownload(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                var databaseStatus = GetDatabaseAccessStatus(
                    connection,
                    transaction,
                    conversationId);
                if (databaseStatus != LocalCacheOperationStatus.Ready)
                {
                    return TransactionResult<LocalCacheOperationStatus>.Rollback(databaseStatus);
                }

                using var command = CreateCommand(connection, transaction, """
                    UPDATE LocalAttachments
                    SET DownloadStatus = $downloaded,
                        LocalPath = $localPath,
                        ThumbnailLocalPath = NULL
                    WHERE Id = $attachmentId
                      AND DownloadStatus = $downloading
                      AND LocalPath IS NULL
                      AND EXISTS (
                          SELECT 1
                          FROM LocalMessages AS message
                          WHERE message.LocalId = LocalAttachments.LocalMessageId
                            AND message.ConversationId = $conversationId
                            AND message.ServerMessageId IS NOT NULL);
                    """);
                AddParameter(command, "$downloaded", DownloadedAttachmentStatus);
                AddParameter(command, "$localPath", managedRelativePath);
                AddParameter(command, "$attachmentId", FormatGuid(attachmentId));
                AddParameter(command, "$downloading", DownloadingAttachmentStatus);
                AddParameter(command, "$conversationId", FormatGuid(conversationId));
                faultInjector?.BeforeAttachmentDownloadCommit();
                return command.ExecuteNonQuery() == 1
                    ? TransactionResult<LocalCacheOperationStatus>.Commit(
                        LocalCacheOperationStatus.Ready)
                    : TransactionResult<LocalCacheOperationStatus>.Rollback(
                        LocalCacheOperationStatus.Conflict);
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Completing an attachment download remained busy after bounded retries; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while completing an " +
                "attachment download after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private LocalCacheOperationStatus FailAttachmentDownload(
        Guid conversationId,
        Guid attachmentId,
        bool canceled)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                var databaseStatus = GetDatabaseAccessStatus(
                    connection,
                    transaction,
                    conversationId);
                if (databaseStatus != LocalCacheOperationStatus.Ready)
                {
                    return TransactionResult<LocalCacheOperationStatus>.Rollback(databaseStatus);
                }

                using var command = CreateCommand(connection, transaction, """
                    UPDATE LocalAttachments
                    SET DownloadStatus = $nextStatus,
                        LocalPath = NULL,
                        ThumbnailLocalPath = NULL
                    WHERE Id = $attachmentId
                      AND DownloadStatus = $downloading
                      AND EXISTS (
                          SELECT 1
                          FROM LocalMessages AS message
                          WHERE message.LocalId = LocalAttachments.LocalMessageId
                            AND message.ConversationId = $conversationId
                            AND message.ServerMessageId IS NOT NULL);
                    """);
                AddParameter(
                    command,
                    "$nextStatus",
                    canceled ? NotDownloadedAttachmentStatus : FailedAttachmentStatus);
                AddParameter(command, "$attachmentId", FormatGuid(attachmentId));
                AddParameter(command, "$downloading", DownloadingAttachmentStatus);
                AddParameter(command, "$conversationId", FormatGuid(conversationId));
                return command.ExecuteNonQuery() == 1
                    ? TransactionResult<LocalCacheOperationStatus>.Commit(
                        LocalCacheOperationStatus.Ready)
                    : TransactionResult<LocalCacheOperationStatus>.Rollback(
                        LocalCacheOperationStatus.Conflict);
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Failing an attachment download remained busy after bounded retries; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while failing an attachment " +
                "download after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private async Task<LocalCacheOperationStatus> InvalidateDownloadedAttachmentCoreAsync(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath,
        bool requireAuthorizedAccess,
        CancellationToken cancellationToken)
    {
        if (IsFatal)
        {
            return LocalCacheOperationStatus.FatalScope;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => InvalidateDownloadedAttachment(
                    conversationId,
                    attachmentId,
                    managedRelativePath,
                    requireAuthorizedAccess))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome == LocalCacheOperationStatus.Ready)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    private LocalCacheOperationStatus InvalidateDownloadedAttachment(
        Guid conversationId,
        Guid attachmentId,
        string managedRelativePath,
        bool requireAuthorizedAccess)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                if (requireAuthorizedAccess)
                {
                    var databaseStatus = GetDatabaseAccessStatus(
                        connection,
                        transaction,
                        conversationId);
                    if (databaseStatus != LocalCacheOperationStatus.Ready)
                    {
                        return TransactionResult<LocalCacheOperationStatus>.Rollback(
                            databaseStatus);
                    }
                }

                using var command = CreateCommand(connection, transaction, """
                    UPDATE LocalAttachments
                    SET DownloadStatus = $notDownloaded,
                        LocalPath = NULL,
                        ThumbnailLocalPath = NULL
                    WHERE Id = $attachmentId
                      AND DownloadStatus = $downloaded
                      AND LocalPath = $localPath
                      AND EXISTS (
                          SELECT 1
                          FROM LocalMessages AS message
                          WHERE message.LocalId = LocalAttachments.LocalMessageId
                            AND message.ConversationId = $conversationId
                            AND message.ServerMessageId IS NOT NULL);
                    """);
                AddParameter(command, "$notDownloaded", NotDownloadedAttachmentStatus);
                AddParameter(command, "$attachmentId", FormatGuid(attachmentId));
                AddParameter(command, "$downloaded", DownloadedAttachmentStatus);
                AddParameter(command, "$localPath", managedRelativePath);
                AddParameter(command, "$conversationId", FormatGuid(conversationId));
                return command.ExecuteNonQuery() == 1
                    ? TransactionResult<LocalCacheOperationStatus>.Commit(
                        LocalCacheOperationStatus.Ready)
                    : TransactionResult<LocalCacheOperationStatus>.Rollback(
                        LocalCacheOperationStatus.Conflict);
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Invalidating an attachment cache entry remained busy after bounded retries; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while invalidating an " +
                "attachment cache entry after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private LocalAttachmentCacheRecoveryOutcome PrepareAttachmentCacheRecovery()
    {
        if (!scopeState.ActiveAttachmentDownloads.IsEmpty)
        {
            return LocalAttachmentCacheRecoveryOutcome.Failure(
                LocalCacheOperationStatus.Conflict);
        }

        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                using (var validate = CreateCommand(connection, transaction, """
                    SELECT attachment.DownloadStatus,
                           attachment.LocalPath,
                           EXISTS (
                               SELECT 1
                               FROM LocalMessages AS message
                               WHERE message.LocalId = attachment.LocalMessageId
                                 AND message.ServerMessageId IS NOT NULL)
                    FROM LocalAttachments AS attachment;
                    """))
                using (var validationReader = validate.ExecuteReader())
                {
                    while (validationReader.Read())
                    {
                        var state = validationReader.GetInt32(0);
                        var hasLocalPath = !validationReader.IsDBNull(1);
                        var isConfirmed = validationReader.GetInt64(2) == 1;
                        if (state is < NotDownloadedAttachmentStatus or > FailedAttachmentStatus ||
                            (state == DownloadedAttachmentStatus) != hasLocalPath ||
                            (state == DownloadedAttachmentStatus && !isConfirmed))
                        {
                            throw new InvalidDataException(
                                "The local cache contains invalid attachment recovery state.");
                        }
                    }
                }

                using (var reset = CreateCommand(connection, transaction, """
                    UPDATE LocalAttachments
                    SET DownloadStatus = $notDownloaded,
                        LocalPath = NULL,
                        ThumbnailLocalPath = NULL
                    WHERE DownloadStatus = $downloading;
                    """))
                {
                    AddParameter(reset, "$notDownloaded", NotDownloadedAttachmentStatus);
                    AddParameter(reset, "$downloading", DownloadingAttachmentStatus);
                    reset.ExecuteNonQuery();
                }

                using var command = CreateCommand(connection, transaction, """
                    SELECT message.ConversationId,
                           attachment.Id,
                           attachment.OriginalFileName,
                           attachment.ContentType,
                           attachment.Size,
                           attachment.DownloadUrl,
                           attachment.DownloadStatus,
                           attachment.LocalPath
                    FROM LocalAttachments AS attachment
                    JOIN LocalMessages AS message
                      ON message.LocalId = attachment.LocalMessageId
                    WHERE message.ServerMessageId IS NOT NULL
                    ORDER BY message.ConversationId, attachment.Id;
                    """);
                var records = new List<LocalAttachmentDownloadRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var record = ReadAttachmentDownloadRecord(reader);
                    if (record.State == LocalAttachmentDownloadState.Downloaded)
                    {
                        records.Add(record);
                    }
                }

                return TransactionResult<LocalAttachmentCacheRecoveryOutcome>.Commit(
                    new LocalAttachmentCacheRecoveryOutcome(
                        LocalCacheOperationStatus.Ready,
                        records.AsReadOnly()));
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Preparing attachment cache recovery remained busy after bounded retries; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalAttachmentCacheRecoveryOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while preparing attachment " +
                "cache recovery after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalAttachmentCacheRecoveryOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private static LocalAttachmentDownloadRecord? ReadAttachmentDownloadRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        Guid attachmentId)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT message.ConversationId,
                   attachment.Id,
                   attachment.OriginalFileName,
                   attachment.ContentType,
                   attachment.Size,
                   attachment.DownloadUrl,
                   attachment.DownloadStatus,
                   attachment.LocalPath
            FROM LocalAttachments AS attachment
            JOIN LocalMessages AS message
              ON message.LocalId = attachment.LocalMessageId
            WHERE attachment.Id = $attachmentId
              AND message.ConversationId = $conversationId
              AND message.ServerMessageId IS NOT NULL
            LIMIT 1;
            """);
        AddParameter(command, "$attachmentId", FormatGuid(attachmentId));
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAttachmentDownloadRecord(reader) : null;
    }

    private static LocalAttachmentDownloadRecord ReadAttachmentDownloadRecord(
        SqliteDataReader reader)
    {
        if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
            conversationId == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(1), "D", out var attachmentId) ||
            attachmentId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The local cache contains invalid attachment identity state.");
        }

        var attachment = new AttachmentDto(
            attachmentId,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            ThumbnailUrl: null);
        if (!ClientAttachmentMetadataPolicy.IsValid(attachment))
        {
            throw new InvalidDataException(
                "The local cache contains invalid attachment metadata.");
        }

        var rawState = reader.GetInt32(6);
        if (rawState is < NotDownloadedAttachmentStatus or > FailedAttachmentStatus)
        {
            throw new InvalidDataException(
                "The local cache contains an invalid attachment download state.");
        }

        var state = (LocalAttachmentDownloadState)rawState;
        var localPath = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (state == LocalAttachmentDownloadState.Downloaded)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new InvalidDataException(
                    "The local cache contains downloaded attachment state without a path.");
            }

            ValidateManagedAttachmentPath(localPath, conversationId, attachmentId);
        }
        else if (localPath is not null)
        {
            throw new InvalidDataException(
                "The local cache contains an attachment path in a non-downloaded state.");
        }

        return new LocalAttachmentDownloadRecord(
            conversationId,
            attachment,
            state,
            localPath);
    }

    private static string ValidateManagedAttachmentPath(
        string managedRelativePath,
        Guid conversationId,
        Guid attachmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRelativePath);
        if (!string.Equals(
                managedRelativePath,
                Path.GetFileName(managedRelativePath),
                StringComparison.Ordinal) ||
            managedRelativePath.Contains(Path.DirectorySeparatorChar) ||
            managedRelativePath.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "The attachment cache path must be a managed relative file name.",
                nameof(managedRelativePath));
        }

        var parts = managedRelativePath.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 ||
            !Guid.TryParseExact(parts[0], "N", out var parsedConversationId) ||
            parsedConversationId != conversationId ||
            !Guid.TryParseExact(parts[1], "N", out var parsedAttachmentId) ||
            parsedAttachmentId != attachmentId ||
            parts[2].Length != 64 ||
            parts[2].Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            !string.Equals(parts[3], "cache", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The attachment cache path is not a valid managed cache file name.",
                nameof(managedRelativePath));
        }

        return parts[2];
    }

    private void PublishAttachmentDownloadCancellationRequested(Guid conversationId)
    {
        Action<Guid>? handlers;
        lock (scopeState.AttachmentEventGate)
        {
            handlers = scopeState.AttachmentDownloadCancellationRequested;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(conversationId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing attachment download cancellation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private void ReleaseAttachmentDownload(Guid conversationId, Guid attachmentId)
    {
        var key = (conversationId, attachmentId);
        if (scopeState.ActiveAttachmentDownloads.TryGetValue(key, out var owner) &&
            owner == cacheInstanceId)
        {
            scopeState.ActiveAttachmentDownloads.TryRemove(key, out _);
        }
    }

    private async Task PublishAttachmentCachePurgedAsync(
        IReadOnlyList<Guid> conversationIds)
    {
        if (conversationIds.Count == 0)
        {
            return;
        }

        Func<Guid, Task>? handlers;
        lock (scopeState.AttachmentEventGate)
        {
            handlers = scopeState.AttachmentCachePurged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var conversationId in conversationIds.Distinct())
        {
            foreach (Func<Guid, Task> handler in handlers.GetInvocationList())
            {
                try
                {
                    await handler(conversationId).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Purging revoked attachment cache files failed; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                }
            }
        }
    }

    private sealed class LocalDownloadedAttachmentAccessActionException(
        Exception innerException)
        : Exception("Downloaded attachment access failed.", innerException);
}
