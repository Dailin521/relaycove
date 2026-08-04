using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Attachments;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Tests.Attachments;

public sealed class WindowsAttachmentOpenServiceTests
{
    [Fact]
    public async Task PrepareAsync_WhenAbortedBeforeCommit_DoesNotExecuteAndReleasesOnStaWorker()
    {
        var backend = new FakeNativeBackend();
        await using var fixture = new ServiceFixture(backend);

        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.Prepared, preparation.Status);
        Assert.True(preparation.Abort());
        var result = await preparation.Completion;
        await backend.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WindowsAttachmentOpenStatus.Aborted, result.Status);
        Assert.Equal(0, backend.AttachmentExecute.ExecuteCount);
        Assert.Equal(
            ["Initialize", "IsWindow", "Create", "SetTitle", "SetGuid", "SetPath", "CheckPolicy", "Release"],
            backend.Calls);
        Assert.Single(backend.CallerThreads);
    }

    [Fact]
    public async Task PrepareAsync_WhenCommitted_ExecutesExactlyOnceAndClosesReturnedProcessHandle()
    {
        var backend = new FakeNativeBackend
        {
            AttachmentExecute = { ProcessHandle = new IntPtr(91) },
        };
        await using var fixture = new ServiceFixture(backend);

        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.Prepared, preparation.Status);
        Assert.True(preparation.Commit());
        Assert.False(preparation.Commit());
        var result = await preparation.Completion;
        await backend.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WindowsAttachmentOpenStatus.Executed, result.Status);
        Assert.Equal(1, backend.AttachmentExecute.ExecuteCount);
        Assert.Equal(1, backend.CloseHandleCount);
        Assert.Equal(
            ["Initialize", "IsWindow", "Create", "SetTitle", "SetGuid", "SetPath", "CheckPolicy", "Execute", "CloseHandle", "Release"],
            backend.Calls);
        Assert.Single(backend.CallerThreads);
        Assert.Equal("RelayCove", backend.AttachmentExecute.ClientTitle);
        Assert.NotEqual(Guid.Empty, backend.AttachmentExecute.ClientGuid);
    }

    [Fact]
    public async Task PrepareAsync_WhenPolicyRejects_ReturnsPolicyRejectedWithoutExecute()
    {
        var backend = new FakeNativeBackend
        {
            AttachmentExecute = { CheckPolicyResult = -1 },
        };
        await using var fixture = new ServiceFixture(backend);

        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.PolicyRejected, preparation.Status);
        Assert.False(preparation.CanCommit);
        Assert.Equal(WindowsAttachmentOpenStatus.PolicyRejected, (await preparation.Completion).Status);
        Assert.Equal(0, backend.AttachmentExecute.ExecuteCount);
    }

    [Theory]
    [InlineData(-2147023673, 7)]
    [InlineData(-2147023741, 8)]
    [InlineData(-1, 9)]
    public async Task PrepareAsync_WhenExecuteFails_ReturnsRedactedStableStatus(
        int executeResult,
        int expectedStatus)
    {
        var backend = new FakeNativeBackend
        {
            AttachmentExecute =
            {
                ExecuteResult = executeResult,
                ProcessHandle = new IntPtr(92),
            },
        };
        await using var fixture = new ServiceFixture(backend);
        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.True(preparation.Commit());

        Assert.Equal((WindowsAttachmentOpenStatus)expectedStatus, (await preparation.Completion).Status);
        await backend.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, backend.AttachmentExecute.ExecuteCount);
        Assert.Equal(1, backend.CloseHandleCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenTwoJobsAreActive_ReturnsBusyWithoutCreatingAThirdAttachmentManager()
    {
        var backend = new FakeNativeBackend
        {
            AttachmentExecute = { BlockExecute = true },
        };
        await using var fixture = new ServiceFixture(backend);
        using var first = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.True(first.Commit());
        await backend.AttachmentExecute.ExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42)).AsTask();
        var third = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.Busy, third.Status);
        Assert.Equal(WindowsAttachmentOpenStatus.Busy, (await third.Completion).Status);
        Assert.Equal(1, backend.Calls.Count(static call => call == "Create"));

        backend.AttachmentExecute.AllowExecute.TrySetResult();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsAttachmentOpenStatus.Prepared, second.Status);
        Assert.True(second.Abort());
        await first.Completion;
        await second.Completion;
    }

    [Fact]
    public async Task DisposeAsync_WhenCommittedJobIsExecuting_ReturnsWithoutCancelingTheJob()
    {
        var backend = new FakeNativeBackend
        {
            AttachmentExecute = { BlockExecute = true },
        };
        var service = new WindowsAttachmentOpenService(
            NullLogger<WindowsAttachmentOpenService>.Instance,
            backend);
        try
        {
            using var preparation = await service.PrepareAsync(CreateLease(), new IntPtr(42));
            Assert.True(preparation.Commit());
            await backend.AttachmentExecute.ExecuteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposal = service.DisposeAsync();

            Assert.True(disposal.IsCompletedSuccessfully);
            Assert.False(preparation.Completion.IsCompleted);
            backend.AttachmentExecute.AllowExecute.TrySetResult();
            Assert.Equal(WindowsAttachmentOpenStatus.Executed, (await preparation.Completion).Status);
        }
        finally
        {
            backend.AttachmentExecute.AllowExecute.TrySetResult();
            await service.DisposeAsync();
            await service.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenOwnerWindowIsInvalid_DoesNotCreateAttachmentManager()
    {
        var backend = new FakeNativeBackend { IsWindowResult = false };
        await using var fixture = new ServiceFixture(backend);

        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.Unavailable, preparation.Status);
        Assert.Equal(WindowsAttachmentOpenStatus.Unavailable, (await preparation.Completion).Status);
        Assert.DoesNotContain("Create", backend.Calls);
        Assert.Equal(0, backend.AttachmentExecute.ExecuteCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenDedicatedStaInitializationChangesMode_FailsClosedWithoutComCalls()
    {
        var backend = new FakeNativeBackend
        {
            InitializeApartmentResult = unchecked((int)0x80010106),
        };
        await using var fixture = new ServiceFixture(backend);

        using var preparation = await fixture.Service.PrepareAsync(CreateLease(), new IntPtr(42));

        Assert.Equal(WindowsAttachmentOpenStatus.Unavailable, preparation.Status);
        Assert.Equal(WindowsAttachmentOpenStatus.Unavailable, (await preparation.Completion).Status);
        Assert.Equal(["Initialize"], backend.Calls);
    }

    [Fact]
    public async Task NativeBackend_WhenWindows_UsesAttachmentManagerForHarmlessTextWithoutExecutingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), $"relaycove-contract-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "RelayCove Attachment Manager contract probe.");
        try
        {
            await RunOnStaAsync(() =>
            {
                var backend = new WindowsAttachmentOpenNativeBackend();
                Assert.True(backend.InitializeApartment() >= 0);
                try
                {
                    Assert.True(backend.CreateAttachmentExecute(out var attachmentExecute) >= 0);
                    Assert.NotNull(attachmentExecute);
                    try
                    {
                        Assert.True(attachmentExecute.SetClientTitle("RelayCove") >= 0);
                        Assert.True(attachmentExecute.SetClientGuid(Guid.NewGuid()) >= 0);
                        Assert.True(attachmentExecute.SetLocalPath(file) >= 0);
                        Assert.True(attachmentExecute.CheckPolicy() >= 0);
                    }
                    finally
                    {
                        backend.ReleaseAttachmentExecute(attachmentExecute);
                    }
                }
                finally
                {
                    backend.UninitializeApartment();
                }
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ClientAttachmentOpenLease CreateLease() =>
        new(owner: null!, localPath: "managed-copy.txt");

    private sealed class ServiceFixture(FakeNativeBackend backend) : IAsyncDisposable
    {
        public WindowsAttachmentOpenService Service { get; } = new(
            NullLogger<WindowsAttachmentOpenService>.Instance,
            backend);

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Service.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakeNativeBackend : IWindowsAttachmentOpenNativeBackend
    {
        private readonly ConcurrentQueue<string> calls = new();
        private readonly ConcurrentDictionary<int, byte> callerThreads = new();

        public FakeAttachmentExecute AttachmentExecute { get; set; } = new();

        public bool IsWindowResult { get; set; } = true;

        public int InitializeApartmentResult { get; set; }

        public int CloseHandleCount { get; private set; }

        public TaskCompletionSource Released { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Calls => calls.ToArray();

        public IReadOnlyCollection<int> CallerThreads => callerThreads.Keys.ToArray();

        public int InitializeApartment()
        {
            Record("Initialize");
            return InitializeApartmentResult;
        }

        public void UninitializeApartment() => Record("Uninitialize");

        public bool IsWindow(IntPtr window)
        {
            Record("IsWindow");
            return IsWindowResult;
        }

        public int CreateAttachmentExecute(out IWindowsAttachmentExecuteNative? attachmentExecute)
        {
            Record("Create");
            attachmentExecute = AttachmentExecute;
            AttachmentExecute.Record = Record;
            return 0;
        }

        public void ReleaseAttachmentExecute(IWindowsAttachmentExecuteNative attachmentExecute)
        {
            Record("Release");
            Released.TrySetResult();
        }

        public bool CloseProcessHandle(IntPtr processHandle)
        {
            Record("CloseHandle");
            CloseHandleCount++;
            return true;
        }

        private void Record(string call)
        {
            calls.Enqueue(call);
            callerThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
        }
    }

    private sealed class FakeAttachmentExecute : IWindowsAttachmentExecuteNative
    {
        public Action<string>? Record { get; set; }

        public string? ClientTitle { get; private set; }

        public Guid ClientGuid { get; private set; }

        public int CheckPolicyResult { get; set; }

        public int ExecuteResult { get; set; }

        public IntPtr ProcessHandle { get; set; }

        public int ExecuteCount { get; private set; }

        public bool BlockExecute { get; set; }

        public TaskCompletionSource ExecuteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExecute { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int SetClientTitle(string title)
        {
            Record?.Invoke("SetTitle");
            ClientTitle = title;
            return 0;
        }

        public int SetClientGuid(Guid clientGuid)
        {
            Record?.Invoke("SetGuid");
            ClientGuid = clientGuid;
            return 0;
        }

        public int SetLocalPath(string localPath)
        {
            Record?.Invoke("SetPath");
            return 0;
        }

        public int CheckPolicy()
        {
            Record?.Invoke("CheckPolicy");
            return CheckPolicyResult;
        }

        public int Execute(IntPtr ownerWindow, out IntPtr processHandle)
        {
            Record?.Invoke("Execute");
            ExecuteCount++;
            ExecuteStarted.TrySetResult();
            if (BlockExecute)
            {
                AllowExecute.Task.GetAwaiter().GetResult();
            }

            processHandle = ProcessHandle;
            return ExecuteResult;
        }
    }
}
