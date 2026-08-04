using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Attachments;

internal sealed class WindowsAttachmentOpenService : IWindowsAttachmentOpenService
{
    private const int HResultUserCanceled = unchecked((int)0x800704C7);
    private const int HResultNoAssociation = unchecked((int)0x80070483);
    private const int MaximumActiveJobs = 1;
    private const string ClientTitle = "RelayCove";
    private static readonly Guid ClientGuid = new("59CC8A1C-F552-4F45-96D5-2E0B7B5CB6F9");

    private readonly object gate = new();
    private readonly Channel<OpenJob> jobs = Channel.CreateUnbounded<OpenJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly HashSet<OpenJob> activeJobs = [];
    private readonly IWindowsAttachmentOpenNativeBackend nativeBackend;
    private readonly ILogger<WindowsAttachmentOpenService> logger;
    private readonly Action? beforeForegroundProtection;
    private readonly TaskCompletionSource started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposalCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    private int workerStopped;

    public WindowsAttachmentOpenService(
        ILogger<WindowsAttachmentOpenService> logger,
        IWindowsAttachmentOpenNativeBackend? nativeBackend = null,
        Action? beforeForegroundProtection = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.nativeBackend = nativeBackend ?? new WindowsAttachmentOpenNativeBackend();
        this.beforeForegroundProtection = beforeForegroundProtection;

        var worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "RelayCove.AttachmentOpen",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    internal Task Stopped => stopped.Task;

    public async ValueTask<WindowsAttachmentOpenPreparation> PrepareAsync(
        ClientAttachmentOpenLease managedOpenCopy,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managedOpenCopy);
        if (ownerWindow == IntPtr.Zero)
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
        }

        string managedOpenCopyPath;
        try
        {
            managedOpenCopyPath = managedOpenCopy.LocalPath;
        }
        catch (ObjectDisposedException)
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
        }

        if (string.IsNullOrWhiteSpace(managedOpenCopyPath))
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
        }

        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref workerStopped) != 0)
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
        }

        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Canceled);
        }
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref workerStopped) != 0)
        {
            return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
        }

        var job = new OpenJob(managedOpenCopyPath, ownerWindow, RemoveJob);
        lock (gate)
        {
            if (activeJobs.Count >= MaximumActiveJobs)
            {
                return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Busy);
            }

            if (Volatile.Read(ref disposed) != 0 ||
                Volatile.Read(ref workerStopped) != 0 ||
                !jobs.Writer.TryWrite(job))
            {
                return CreateUnavailablePreparation(WindowsAttachmentOpenStatus.Unavailable);
            }

            activeJobs.Add(job);
        }

        try
        {
            var staged = await job.Staged.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return staged == WindowsAttachmentOpenStatus.Prepared
                ? new WindowsAttachmentOpenPreparation(staged, job, job.Completion.Task)
                : new WindowsAttachmentOpenPreparation(staged, null, job.Completion.Task);
        }
        catch (OperationCanceledException)
        {
            job.Abort();
            return new WindowsAttachmentOpenPreparation(
                WindowsAttachmentOpenStatus.Canceled,
                null,
                job.Completion.Task);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return new ValueTask(disposalCompletion.Task);
        }

        OpenJob[] jobsToAbort;
        lock (gate)
        {
            jobsToAbort = [.. activeJobs];
            jobs.Writer.TryComplete();
            var disposal = DisposeActiveJobs(jobsToAbort);
            if (disposal.IsCompletedSuccessfully)
            {
                disposalCompletion.TrySetResult();
            }
            else
            {
                _ = CompleteDisposalAsync(disposal);
            }
        }

        return new ValueTask(disposalCompletion.Task);
    }

    private async Task CompleteDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
            disposalCompletion.TrySetResult();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            disposalCompletion.TrySetException(exception);
        }
    }

    private static Task DisposeActiveJobs(OpenJob[] jobsToAbort)
    {
        foreach (var job in jobsToAbort)
        {
            job.Abort();
        }

        var committedJobs = jobsToAbort.Where(static job => job.IsCommitted).ToArray();
        return WaitForCommittedJobsToAttemptExecuteAsync(committedJobs);
    }

    private static Task WaitForCommittedJobsToAttemptExecuteAsync(OpenJob[] committedJobs) =>
        committedJobs.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(committedJobs.Select(static job => job.ExecuteAttemptOrTerminal.Task));

    private void WorkerMain()
    {
        var initializeResult = unchecked((int)0x80004005);
        var uninitialize = false;
        try
        {
            initializeResult = nativeBackend.InitializeApartment();
            uninitialize = initializeResult >= 0;
            started.TrySetResult();
            ReadJobs(initializeResult >= 0);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            logger.LogWarning("Windows attachment open worker became unavailable.");
            Interlocked.Exchange(ref workerStopped, 1);
            started.TrySetResult();
            DrainUnavailableJobs();
        }
        finally
        {
            if (uninitialize)
            {
                try
                {
                    nativeBackend.UninitializeApartment();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    logger.LogWarning("Windows attachment open worker cleanup failed.");
                }
            }

            Interlocked.Exchange(ref workerStopped, 1);
            started.TrySetResult();
            stopped.TrySetResult();
        }
    }

    private void ReadJobs(bool apartmentAvailable)
    {
        try
        {
            while (jobs.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (jobs.Reader.TryRead(out var job))
                {
                    ProcessJob(job, apartmentAvailable);
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            logger.LogWarning("Windows attachment open worker stopped accepting jobs.");
            Interlocked.Exchange(ref workerStopped, 1);
            DrainUnavailableJobs();
        }
    }

    private void ProcessJob(OpenJob job, bool apartmentAvailable)
    {
        IWindowsAttachmentExecuteNative? attachmentExecute = null;
        var foregroundExecution = false;
        try
        {
            if (job.IsAborted)
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Aborted);
                return;
            }

            if (!apartmentAvailable || !nativeBackend.IsWindow(job.OwnerWindow))
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
                return;
            }

            var createResult = nativeBackend.CreateAttachmentExecute(out attachmentExecute);
            if (job.IsAborted)
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Aborted);
                return;
            }

            if (createResult < 0 || attachmentExecute is null)
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
                return;
            }

            if (attachmentExecute.SetClientTitle(ClientTitle) < 0 ||
                attachmentExecute.SetClientGuid(ClientGuid) < 0 ||
                attachmentExecute.SetLocalPath(job.ManagedOpenCopyPath) < 0)
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
                return;
            }

            if (attachmentExecute.CheckPolicy() < 0)
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.PolicyRejected);
                return;
            }

            if (!job.CompletePreparation(WindowsAttachmentOpenStatus.Prepared))
            {
                return;
            }

            var decision = job.Decision.Task.GetAwaiter().GetResult();
            if (decision != OpenJobDecision.Committed)
            {
                job.CompleteExecution(WindowsAttachmentOpenStatus.Aborted);
                return;
            }

            try
            {
                // A background STA may be torn down during normal WPF process
                // shutdown between managed acknowledgement and the COM call. Keep
                // this one committed launch alive through Execute, handle/COM
                // release, completion, and retirement; DisposeAsync still waits
                // only for the native Execute boundary acknowledgement.
                beforeForegroundProtection?.Invoke();
                Thread.CurrentThread.IsBackground = false;
                foregroundExecution = true;
                job.CompleteForegroundProtection(succeeded: true);

                // Commit acknowledgement intentionally precedes the COM call.
                // The coordinator releases this only after its SQLite transaction,
                // commit gate, and shell identity gate have all unwound.
                job.ExecuteRelease.Task.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                logger.LogWarning(
                    "Windows attachment open worker could not retain a committed execution.");
                job.CompleteForegroundProtection(succeeded: false);
                job.CompleteExecution(WindowsAttachmentOpenStatus.ExecuteFailed);
                return;
            }

            ExecuteOnce(attachmentExecute, job);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            logger.LogWarning("Windows attachment open operation failed.");
            if (job.IsPrepared)
            {
                job.CompleteExecution(WindowsAttachmentOpenStatus.ExecuteFailed);
            }
            else
            {
                job.CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
            }
        }
        finally
        {
            if (attachmentExecute is not null)
            {
                try
                {
                    nativeBackend.ReleaseAttachmentExecute(attachmentExecute);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    logger.LogWarning("Windows attachment open operation cleanup failed.");
                }
            }

            job.CompleteIfNeeded();
            job.CompleteForegroundProtection(succeeded: false);
            job.CompleteAfterRelease();
            job.Retire();
            if (foregroundExecution)
            {
                try
                {
                    Thread.CurrentThread.IsBackground = true;
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    logger.LogWarning("Windows attachment open worker background restore failed.");
                }
            }
        }
    }

    private void ExecuteOnce(IWindowsAttachmentExecuteNative attachmentExecute, OpenJob job)
    {
        var processHandle = IntPtr.Zero;
        var executeResult = unchecked((int)0x80004005);
        try
        {
            executeResult = attachmentExecute.Execute(
                job.OwnerWindow,
                job.MarkExecuteAttempted,
                out processHandle);
        }
        finally
        {
            if (processHandle != IntPtr.Zero && processHandle != new IntPtr(-1))
            {
                try
                {
                    nativeBackend.CloseProcessHandle(processHandle);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    logger.LogWarning("Windows attachment open process signal cleanup failed.");
                }
            }
        }

        job.CompleteExecution(MapExecuteResult(executeResult));
    }

    private void DrainUnavailableJobs()
    {
        while (jobs.Reader.TryRead(out var job))
        {
            job.CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
            job.CompleteIfNeeded();
            job.CompleteAfterRelease();
            job.Retire();
        }
    }

    private void RemoveJob(OpenJob job)
    {
        lock (gate)
        {
            activeJobs.Remove(job);
        }
    }

    private static WindowsAttachmentOpenPreparation CreateUnavailablePreparation(
        WindowsAttachmentOpenStatus status) =>
        new(
            status,
            null,
            Task.FromResult(new WindowsAttachmentOpenResult(status)));

    private static WindowsAttachmentOpenStatus MapExecuteResult(int result) => result switch
    {
        >= 0 => WindowsAttachmentOpenStatus.Executed,
        HResultUserCanceled => WindowsAttachmentOpenStatus.UserCanceled,
        HResultNoAssociation => WindowsAttachmentOpenStatus.NoAssociation,
        _ => WindowsAttachmentOpenStatus.ExecuteFailed,
    };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    internal sealed class OpenJob
    {
        private readonly Action<OpenJob> onCompleted;
        private int staged;
        private int completed;
        private int retired;
        private int decision;
        private int executeAttempted;
        private WindowsAttachmentOpenStatus? terminalStatus;

        public OpenJob(string managedOpenCopyPath, IntPtr ownerWindow, Action<OpenJob> onCompleted)
        {
            ManagedOpenCopyPath = managedOpenCopyPath;
            OwnerWindow = ownerWindow;
            this.onCompleted = onCompleted;
        }

        public string ManagedOpenCopyPath { get; }

        public IntPtr OwnerWindow { get; }

        public TaskCompletionSource<WindowsAttachmentOpenStatus> Staged { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<OpenJobDecision> Decision { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<WindowsAttachmentOpenResult> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExecuteAttemptOrTerminal { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExecuteRelease { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> ForegroundProtection { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAborted => Volatile.Read(ref decision) == (int)OpenJobDecision.Aborted;

        public bool IsCommitted => Volatile.Read(ref decision) == (int)OpenJobDecision.Committed;

        public bool IsPrepared => Volatile.Read(ref staged) != 0;

        public bool Commit()
        {
            if (Interlocked.CompareExchange(
                    ref decision,
                    (int)OpenJobDecision.Committed,
                    (int)OpenJobDecision.None) != (int)OpenJobDecision.None)
            {
                return false;
            }

            Decision.TrySetResult(OpenJobDecision.Committed);
            return ForegroundProtection.Task.GetAwaiter().GetResult();
        }

        public bool Abort()
        {
            if (Interlocked.CompareExchange(
                    ref decision,
                    (int)OpenJobDecision.Aborted,
                    (int)OpenJobDecision.None) != (int)OpenJobDecision.None)
            {
                return false;
            }

            Decision.TrySetResult(OpenJobDecision.Aborted);
            return true;
        }

        public bool ReleaseExecute() => ExecuteRelease.TrySetResult();

        public bool CompletePreparation(WindowsAttachmentOpenStatus status)
        {
            if (Interlocked.CompareExchange(ref staged, 1, 0) != 0)
            {
                return false;
            }

            Staged.TrySetResult(status);
            if (status != WindowsAttachmentOpenStatus.Prepared)
            {
                CompleteExecution(status);
            }

            return true;
        }

        public void CompleteExecution(WindowsAttachmentOpenStatus status)
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                terminalStatus = status;
                if (Volatile.Read(ref executeAttempted) == 0)
                {
                    ExecuteAttemptOrTerminal.TrySetResult();
                }
            }
        }

        public void MarkExecuteAttempted()
        {
            if (Interlocked.Exchange(ref executeAttempted, 1) == 0)
            {
                ExecuteAttemptOrTerminal.TrySetResult();
            }
        }

        public void CompleteForegroundProtection(bool succeeded) =>
            ForegroundProtection.TrySetResult(succeeded);

        internal void FailForegroundProtection(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ForegroundProtection.TrySetException(exception);
        }

        public void CompleteAfterRelease()
        {
            CompleteIfNeeded();
            Completion.TrySetResult(new WindowsAttachmentOpenResult(
                terminalStatus ?? WindowsAttachmentOpenStatus.Unavailable));
        }

        public void Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) == 0)
            {
                onCompleted(this);
            }
        }

        public void CompleteIfNeeded()
        {
            if (Volatile.Read(ref staged) == 0)
            {
                CompletePreparation(WindowsAttachmentOpenStatus.Unavailable);
            }
            else if (Volatile.Read(ref completed) == 0)
            {
                CompleteExecution(WindowsAttachmentOpenStatus.Aborted);
            }
        }
    }

    internal enum OpenJobDecision
    {
        None = 0,
        Committed = 1,
        Aborted = 2,
    }
}
