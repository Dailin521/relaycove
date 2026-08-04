using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Accounts;
using RelayCove.Client.Attachments;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Realtime;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Tests.Accounts;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientAccountRuntimeTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        3,
        18,
        0,
        0,
        TimeSpan.Zero);
    private static readonly Guid UserId =
        Guid.Parse("b1775924-a66e-4d9b-a6f6-e767b084be2f");
    private static readonly Uri ServerBaseUri = new("https://example.com/proxy/");
    private const string AccessToken = "runtime.access.token";
    private const string RefreshToken = "runtime-refresh-token";

    [Fact]
    public async Task Factory_WhenSessionIsAuthenticated_CreatesMatchingScopeAndRealCache()
    {
        using var directory = new TemporaryDirectory();
        var session = CreateSession();
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(new DelegateHttpHandler((_, _) =>
                throw new InvalidOperationException("HTTP must not be called during creation."))),
            directory.Path,
            NullLoggerFactory.Instance);

        var runtime = await factory.CreateAsync(session);
        var expectedIdentity = AccountScopeIdentity.Create(
            ServerBaseUri,
            UserId,
            directory.Path);

        Assert.Equal(expectedIdentity.Id, runtime.Identity.Id);
        Assert.Equal(expectedIdentity.DatabasePath, runtime.Identity.DatabasePath);
        Assert.True(File.Exists(runtime.Identity.DatabasePath));
        Assert.DoesNotContain("example.com", runtime.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserId.ToString(), runtime.ToString(), StringComparison.OrdinalIgnoreCase);

        await runtime.DisposeAsync();
        Assert.True(session.IsDisposeCompleted);
    }

    [Fact]
    public async Task Factory_WhenSessionIsNotAuthenticated_FailsBeforeCreatingScopeDirectory()
    {
        using var directory = new TemporaryDirectory();
        var accountRoot = System.IO.Path.Combine(directory.Path, "accounts");
        var session = CreateSession();
        await session.DisposeAsync();
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(new DelegateHttpHandler((_, _) =>
                throw new InvalidOperationException("HTTP must not be called."))),
            accountRoot,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(session));

        Assert.False(Directory.Exists(accountRoot));
    }

    [Fact]
    public async Task Factory_WhenComponentCreationFails_ReleasesCacheAndLeavesSessionCallerOwned()
    {
        using var directory = new TemporaryDirectory();
        var accountRoot = System.IO.Path.Combine(directory.Path, "accounts");
        var session = CreateSession();
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(new DelegateHttpHandler((_, _) =>
                throw new InvalidOperationException("HTTP must not be called."))),
            accountRoot,
            NullLoggerFactory.Instance,
            (_, _, _, _) => throw new IOException("classified construction failure"));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            factory.CreateAsync(session));

        Assert.Equal("classified construction failure", exception.Message);
        SqliteConnection.ClearAllPools();
        Directory.Delete(accountRoot, recursive: true);
        Assert.False(Directory.Exists(accountRoot));
        Assert.True(session.IsAuthenticated);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FactoryRuntime_WhenStarted_UsesRealtimeBeforeRealCacheAndHttpSync()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var handler = new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(AccessToken, request.Headers.Authorization.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/conversations",
                    StringComparison.Ordinal))
            {
                order.Enqueue("http-conversations");
                return Task.FromResult(Ok(
                    new ConversationListResponse([], Complete: true)));
            }

            Assert.EndsWith("/api/sync", request.RequestUri.AbsolutePath);
            order.Enqueue("http-sync");
            return Task.FromResult(Ok(
                new SyncResponse([], 0, 0, HasMore: false)));
        });
        var realtime = new FakeRealtimeConnection
        {
            StartAction = _ =>
            {
                order.Enqueue("realtime-start");
                return Task.CompletedTask;
            },
        };
        Uri? capturedServerBaseUri = null;
        Func<Task<string?>>? capturedAccessTokenProvider = null;
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(handler),
            directory.Path,
            NullLoggerFactory.Instance,
            (serverBaseUri, accessTokenProvider, _, _) =>
            {
                capturedServerBaseUri = serverBaseUri;
                capturedAccessTokenProvider = accessTokenProvider;
                return realtime;
            });
        var session = CreateSession();
        var runtime = await factory.CreateAsync(session);
        var unreadTarget = ClientNotificationActivationTarget.UnreadOverview(
            runtime.Identity.Id);

        Assert.False(runtime.TryAuthorizeNotificationTarget(unreadTarget));

        var outcome = await runtime.StartAsync();

        Assert.True(outcome.IsAuthoritativeCacheReady);
        Assert.True(runtime.TryAuthorizeNotificationTarget(unreadTarget));
        Assert.Equal(ServerBaseUri, capturedServerBaseUri);
        Assert.Equal(AccessToken, await capturedAccessTokenProvider!());
        Assert.Equal(
            ["realtime-start", "http-conversations", "http-sync"],
            order);
        Assert.True(File.Exists(runtime.Identity.DatabasePath));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task FactoryRuntime_WhenStarted_ExposesConversationSnapshotAndContinuousState()
    {
        using var directory = new TemporaryDirectory();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Runtime conversation",
            null,
            Now.AddHours(-2),
            Now.AddHours(-1),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 2,
            IsMuted: true);
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith(
                "/api/conversations",
                StringComparison.Ordinal)
                ? Ok(new ConversationListResponse([conversation], Complete: true))
                : Ok(new SyncResponse([], 0, 0, HasMore: false))));
        var realtime = new FakeRealtimeConnection();
        IRealtimeEventSink? capturedSink = null;
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(handler),
            directory.Path,
            NullLoggerFactory.Instance,
            (_, _, sink, _) =>
            {
                capturedSink = sink;
                return realtime;
            });
        var runtime = await factory.CreateAsync(CreateSession());
        var conversationRevisions = new ConcurrentQueue<long>();
        var connectionStates = new ConcurrentQueue<ConnectionState>();
        runtime.ConversationStateChanged += conversationRevisions.Enqueue;
        runtime.ConnectionStateChanged += connectionStates.Enqueue;

        var start = await runtime.StartAsync();
        var list = await runtime.ReadConversationListAsync();
        await capturedSink!.OnConnectionStateChangedAsync(
            ConnectionState.Reconnecting,
            CancellationToken.None);

        Assert.True(start.IsAuthoritativeCacheReady);
        Assert.Equal(LocalCacheOperationStatus.Ready, list.Status);
        Assert.Equal(2, list.TotalUnreadCount);
        Assert.Equal(conversation.Id, Assert.Single(list.Conversations).Id);
        Assert.NotEmpty(conversationRevisions);
        Assert.Equal([ConnectionState.Reconnecting], connectionStates);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task FactoryRuntime_WhenAttachmentDownloads_UsesDedicatedTransferClientAndCacheRoot()
    {
        using var directory = new TemporaryDirectory();
        var accountRoot = Path.Combine(directory.Path, "accounts");
        var cacheRoot = Path.Combine(directory.Path, "cache");
        var payload = "runtime attachment"u8.ToArray();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Attachment conversation",
            null,
            Now.AddHours(-2),
            Now.AddHours(-1),
            LastMessageId: 1,
            LastReadMessageId: 0,
            UnreadCount: 1);
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "runtime.bin",
            "application/octet-stream",
            payload.LongLength,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(conversation.Id) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var normalRequests = 0;
        using var normalClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Interlocked.Increment(ref normalRequests);
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/conversations",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(
                    new ConversationListResponse([conversation], Complete: true)));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/api/sync", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(
                    new SyncResponse([message], 1, 1, HasMore: false)));
            }

            throw new InvalidOperationException(
                "Attachment download must not use the normal HTTP client.");
        }));
        var transferRequests = 0;
        using var transferClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Interlocked.Increment(ref transferRequests);
            Assert.EndsWith(attachment.DownloadUrl, request.RequestUri!.AbsolutePath);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            response.Headers.ETag = new EntityTagHeaderValue($"\"{hash}\"");
            return Task.FromResult(response);
        }));
        var attachmentShell = new FakeWindowsAttachmentShell();
        var factory = new ClientAccountRuntimeFactory(
            normalClient,
            accountRoot,
            NullLoggerFactory.Instance,
            (_, _, _, _) => new FakeRealtimeConnection(),
            attachmentUploadHttpClient: transferClient,
            attachmentCacheRootDirectory: cacheRoot,
            attachmentShell: attachmentShell);
        var runtime = await factory.CreateAsync(CreateSession());
        Assert.True((await runtime.StartAsync()).IsAuthoritativeCacheReady);

        var outcome = await runtime.DownloadAttachmentAsync(
            conversation.Id,
            attachment.Id);
        var reveal = await runtime.RevealAttachmentInFolderAsync(
            conversation.Id,
            attachment.Id,
            () => ClientAttachmentRevealStatus.Revealed);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, outcome.Status);
        Assert.Equal(ClientAttachmentRevealStatus.Revealed, reveal.Status);
        Assert.Equal(1, attachmentShell.RevealCount);
        Assert.Equal(2, Volatile.Read(ref normalRequests));
        Assert.Equal(1, Volatile.Read(ref transferRequests));
        Assert.True(File.Exists(Path.Combine(
            cacheRoot,
            runtime.Identity.Id,
            outcome.LocalPath!)));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenRevealShellStarted_DoesNotWaitForNativeShellReturn()
    {
        using var directory = new TemporaryDirectory();
        var shellStarted = NewSignal();
        var shellRelease = NewSignal();
        var attachmentCoordinator = new FakeAttachmentDownloadCoordinator
        {
            RevealAction = async (_, _, commit, _) =>
            {
                var status = commit();
                if (status != ClientAttachmentRevealStatus.Revealed)
                {
                    return ClientAttachmentRevealOutcome.FromStatus(status);
                }

                shellStarted.TrySetResult();
                await shellRelease.Task;
                return ClientAttachmentRevealOutcome.FromStatus(
                    ClientAttachmentRevealStatus.Revealed);
            },
        };
        var cacheDisposed = false;
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            cache: new RecordingAsyncDisposable(() =>
            {
                cacheDisposed = true;
                return ValueTask.CompletedTask;
            }),
            attachmentDownloadCoordinator: attachmentCoordinator);

        var reveal = runtime.RevealAttachmentInFolderAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            () => ClientAttachmentRevealStatus.Revealed);
        await shellStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cacheDisposed);
        Assert.True(attachmentCoordinator.IsDisposeCompleted);

        shellRelease.TrySetResult();
        Assert.Equal(ClientAttachmentRevealStatus.Revealed, (await reveal).Status);
    }

    [Fact]
    public async Task LoadAttachmentImageAsync_WhenCoordinatorIsAvailable_ForwardsExactIdentityRenditionAndCommit()
    {
        using var directory = new TemporaryDirectory();
        var expectedConversationId = Guid.NewGuid();
        var expectedAttachmentId = Guid.NewGuid();
        Guid? actualConversationId = null;
        Guid? actualAttachmentId = null;
        ClientAttachmentImageRendition? actualRendition = null;
        var attachmentCoordinator = new FakeAttachmentDownloadCoordinator
        {
            ImageLoadAction = (conversationId, attachmentId, rendition, commit, _) =>
            {
                actualConversationId = conversationId;
                actualAttachmentId = attachmentId;
                actualRendition = rendition;
                return Task.FromResult(ClientAttachmentImageLoadOutcome.Failure(
                    commit() == ClientAttachmentImageLoadStatus.Ready
                        ? ClientAttachmentImageLoadStatus.UnsupportedFormat
                        : ClientAttachmentImageLoadStatus.Stale));
            },
        };
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            attachmentDownloadCoordinator: attachmentCoordinator);

        var outcome = await runtime.LoadAttachmentImageAsync(
            expectedConversationId,
            expectedAttachmentId,
            ClientAttachmentImageRendition.Viewer,
            () => ClientAttachmentImageLoadStatus.Ready);

        Assert.Equal(ClientAttachmentImageLoadStatus.UnsupportedFormat, outcome.Status);
        Assert.Equal(expectedConversationId, actualConversationId);
        Assert.Equal(expectedAttachmentId, actualAttachmentId);
        Assert.Equal(ClientAttachmentImageRendition.Viewer, actualRendition);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenImageLoadIsInFlight_CancelsCoordinatorBeforeCacheDisposal()
    {
        using var directory = new TemporaryDirectory();
        var imageStarted = NewSignal();
        var imageCanceled = NewSignal();
        var cacheDisposed = false;
        var attachmentCoordinator = new FakeAttachmentDownloadCoordinator
        {
            ImageLoadAction = async (_, _, _, _, cancellationToken) =>
            {
                imageStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    imageCanceled.TrySetResult();
                    throw;
                }

                throw new InvalidOperationException("The cancellation path must throw.");
            },
        };
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            cache: new RecordingAsyncDisposable(() =>
            {
                Assert.True(imageCanceled.Task.IsCompleted);
                cacheDisposed = true;
                return ValueTask.CompletedTask;
            }),
            attachmentDownloadCoordinator: attachmentCoordinator);

        var imageLoad = runtime.LoadAttachmentImageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ClientAttachmentImageRendition.Thumbnail,
            () => ClientAttachmentImageLoadStatus.Ready);
        await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => imageLoad);
        Assert.True(cacheDisposed);
        Assert.True(attachmentCoordinator.IsDisposeCompleted);
    }

    [Fact]
    public async Task FactoryRuntime_WhenNotificationPlatformAccepts_WiresStartupSummaryAndPersistsHandling()
    {
        using var directory = new TemporaryDirectory();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Notification conversation",
            null,
            Now.AddHours(-2),
            Now.AddHours(-1),
            0,
            0,
            0);
        var handler = new DelegateHttpHandler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(
                "/api/conversations",
                StringComparison.Ordinal)
                    ? Ok(new ConversationListResponse([conversation], Complete: true))
                    : Ok(new SyncResponse(
                        [CreateMessage(conversation.Id)],
                        1,
                        1,
                        HasMore: false))));
        var platform = new RecordingNotificationPlatform();
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(handler),
            directory.Path,
            NullLoggerFactory.Instance,
            (_, _, _, _) => new FakeRealtimeConnection(),
            platform,
            static () => ClientNotificationSettingsSnapshot.Enabled);
        var runtime = await factory.CreateAsync(CreateSession());

        var outcome = await runtime.StartAsync();

        Assert.True(outcome.IsAuthoritativeCacheReady);
        var request = Assert.Single(platform.Requests);
        Assert.Equal(NotificationPolicy.Summary, request.Policy);
        Assert.Equal(1, Scalar(runtime.Identity, "SELECT IsNotificationHandled FROM LocalMessages;"));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task FactoryRuntime_WhenInjectedPlatformIsUnavailable_LeavesCandidateForRecovery()
    {
        using var directory = new TemporaryDirectory();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Deferred notification conversation",
            null,
            Now.AddHours(-2),
            Now.AddHours(-1),
            0,
            0,
            0);
        var handler = new DelegateHttpHandler((request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(
                "/api/conversations",
                StringComparison.Ordinal)
                    ? Ok(new ConversationListResponse([conversation], Complete: true))
                    : Ok(new SyncResponse(
                        [CreateMessage(conversation.Id)],
                        1,
                        1,
                        HasMore: false))));
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(handler),
            directory.Path,
            NullLoggerFactory.Instance,
            (_, _, _, _) => new FakeRealtimeConnection(),
            new DeferredClientNotificationPlatform(),
            static () => ClientNotificationSettingsSnapshot.Unavailable);
        var runtime = await factory.CreateAsync(CreateSession());

        var outcome = await runtime.StartAsync();

        Assert.True(outcome.IsAuthoritativeCacheReady);
        Assert.Equal(0, Scalar(runtime.Identity, "SELECT IsNotificationHandled FROM LocalMessages;"));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenConcurrentAndOneWaiterCancels_StartsRealtimeThenStartupSyncOnce()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var realtimeEntered = NewSignal();
        var releaseRealtime = NewSignal();
        var realtime = new FakeRealtimeConnection
        {
            StartAction = async cancellationToken =>
            {
                order.Enqueue("realtime-start");
                realtimeEntered.TrySetResult();
                await releaseRealtime.Task.WaitAsync(cancellationToken);
            },
        };
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (reason, _) =>
            {
                order.Enqueue($"sync-{reason}");
                return Task.FromResult(Completed(reason));
            },
        };
        var session = CreateSession();
        var runtime = CreateRuntime(directory.Path, session, realtime, sync);
        using var callerCancellation = new CancellationTokenSource();

        var canceledWaiter = runtime.StartAsync(callerCancellation.Token);
        var survivingWaiters = Enumerable.Range(0, 19)
            .Select(_ => runtime.StartAsync())
            .ToArray();
        await realtimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        releaseRealtime.TrySetResult();

        var outcomes = await Task.WhenAll(survivingWaiters);
        Assert.All(outcomes, outcome => Assert.True(outcome.IsAuthoritativeCacheReady));
        Assert.Equal(1, realtime.StartCount);
        Assert.Equal(["realtime-start", "sync-Startup"], order);
        Assert.Equal([SyncReason.Startup], sync.Reasons);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenRealtimeFails_StillRunsStartupSync()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var realtime = new FakeRealtimeConnection
        {
            StateValue = ConnectionState.ServerUnavailable,
            StartAction = _ =>
            {
                order.Enqueue("realtime-start");
                throw new HttpRequestException("classified realtime detail");
            },
        };
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (reason, _) =>
            {
                order.Enqueue($"sync-{reason}");
                return Task.FromResult(Completed(reason));
            },
        };
        var session = CreateSession();
        var logger = new RecordingLogger<ClientAccountRuntime>();
        var runtime = CreateRuntime(directory.Path, session, realtime, sync, logger: logger);

        var outcome = await runtime.StartAsync();

        Assert.Equal(ConnectionState.ServerUnavailable, outcome.RealtimeState);
        Assert.True(outcome.IsAuthoritativeCacheReady);
        Assert.Equal(["realtime-start", "sync-Startup"], order);
        var diagnosticText = runtime + " " + outcome + " " + string.Join(' ', logger.Entries);
        Assert.DoesNotContain("classified realtime detail", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", diagnosticText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserId.ToString(), diagnosticText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AccessToken, diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, diagnosticText, StringComparison.Ordinal);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitOperations_WhenStartIsInProgress_RejectUntilStartupCompletes()
    {
        using var directory = new TemporaryDirectory();
        var realtimeEntered = NewSignal();
        var releaseRealtime = NewSignal();
        var realtime = new FakeRealtimeConnection
        {
            StartAction = async cancellationToken =>
            {
                realtimeEntered.TrySetResult();
                await releaseRealtime.Task.WaitAsync(cancellationToken);
            },
        };
        var sync = new FakeSyncCoordinator();
        var session = CreateSession();
        var runtime = CreateRuntime(directory.Path, session, realtime, sync);

        var startup = runtime.StartAsync();
        await realtimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.TriggerSyncAsync(SyncReason.WindowActivated));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RetryRealtimeAsync());
        releaseRealtime.TrySetResult();
        await startup;

        await runtime.TriggerSyncAsync(SyncReason.WindowActivated);
        await runtime.RetryRealtimeAsync();
        Assert.Equal(
            [SyncReason.Startup, SyncReason.WindowActivated, SyncReason.Reconnect],
            sync.Reasons);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task AutomaticSync_WhenRuntimeStarts_UsesInitialActivityAsBaseline()
    {
        using var directory = new TemporaryDirectory();
        var sync = new FakeSyncCoordinator();
        var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            NullLogger<ClientAutomaticSyncScheduler>.Instance,
            delayAsync: static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            sync,
            automaticSyncScheduler: scheduler);
        var foreground = new ClientActivitySnapshot(
            IsMainWindowVisible: true,
            IsMainWindowMinimized: false,
            HasForegroundFocus: true,
            OpenConversationId: null);
        runtime.UpdateActivity(foreground);

        await runtime.StartAsync();
        runtime.UpdateActivity(foreground);
        runtime.UpdateActivity(ClientActivitySnapshot.Inactive);
        runtime.UpdateActivity(foreground);

        Assert.Equal(
            [SyncReason.Startup, SyncReason.WindowActivated],
            sync.Reasons);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task AutomaticSync_WhenStartupIsPending_DoesNotStartPeriodicClock()
    {
        using var directory = new TemporaryDirectory();
        var releaseStartup = new TaskCompletionSource<ClientSyncRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (reason, _) => reason == SyncReason.Startup
                ? releaseStartup.Task
                : Task.FromResult(Completed(reason)),
        };
        var clockStarted = NewSignal();
        var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            NullLogger<ClientAutomaticSyncScheduler>.Instance,
            delayAsync: (_, cancellationToken) =>
            {
                clockStarted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            sync,
            automaticSyncScheduler: scheduler);

        var startup = runtime.StartAsync();
        await WaitUntilAsync(() => sync.Reasons.Count == 1);
        Assert.False(clockStarted.Task.IsCompleted);
        releaseStartup.TrySetResult(Completed(SyncReason.Startup));
        await startup;
        await clockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CancelsAutomaticClockBeforeRealtimeAndSyncCleanup()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var clockStarted = NewSignal();
        var sync = new FakeSyncCoordinator
        {
            DisposeAction = () =>
            {
                order.Enqueue("sync-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            NullLogger<ClientAutomaticSyncScheduler>.Instance,
            delayAsync: (_, cancellationToken) =>
            {
                clockStarted.TrySetResult();
                return WaitForClockCancellationAsync(cancellationToken, order);
            });
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection
            {
                DisposeAction = () =>
                {
                    order.Enqueue("realtime-dispose");
                    return ValueTask.CompletedTask;
                },
            },
            sync,
            automaticSyncScheduler: scheduler);
        await runtime.StartAsync();
        await clockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync();

        Assert.Equal(
            ["automatic-clock-canceled", "realtime-dispose", "sync-dispose"],
            order);
    }

    [Fact]
    public async Task RealtimeSink_WhenAutomaticReconnect_ReturnsWithoutWaitingForRequestedSync()
    {
        var inner = new RecordingRealtimeSink();
        var syncCompletion = new TaskCompletionSource<ClientSyncRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (_, _) => syncCompletion.Task,
        };
        var requestor = new ClientAccountSyncRequestor(
            sync,
            NullLogger<ClientAccountSyncRequestor>.Instance);
        var sink = new ClientAccountRealtimeEventSink(inner, requestor);

        await sink.OnConnectionStateChangedAsync(
            ConnectionState.Connected,
            CancellationToken.None);
        Assert.Empty(sync.Reasons);

        await sink.OnConnectionStateChangedAsync(
            ConnectionState.Reconnecting,
            CancellationToken.None);
        var connected = sink.OnConnectionStateChangedAsync(
            ConnectionState.Connected,
            CancellationToken.None);

        await connected.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([SyncReason.Reconnect], sync.Reasons);
        Assert.Equal(
            [
                ConnectionState.Connected,
                ConnectionState.Reconnecting,
                ConnectionState.Connected,
            ],
            inner.States);
        syncCompletion.TrySetResult(Completed(SyncReason.Reconnect));
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task RealtimeSink_WhenConversationIsUnknown_RejectsMessageAndRequestsSyncWithoutWaiting()
    {
        using var directory = new TemporaryDirectory();
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, directory.Path);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var syncCompletion = new TaskCompletionSource<ClientSyncRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (_, _) => syncCompletion.Task,
        };
        var requestor = new ClientAccountSyncRequestor(
            sync,
            NullLogger<ClientAccountSyncRequestor>.Instance);
        var cacheSink = new LocalCacheRealtimeEventSink(
            cache,
            (_, _) =>
            {
                requestor.Request(SyncReason.Reconnect);
                return Task.CompletedTask;
            },
            NullLogger<LocalCacheRealtimeEventSink>.Instance);
        var sink = new ClientAccountRealtimeEventSink(cacheSink, requestor);

        var eventHandling = sink.OnNewMessageAsync(
            CreateMessage(Guid.NewGuid()),
            CancellationToken.None);

        await eventHandling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([SyncReason.Reconnect], sync.Reasons);
        Assert.False(syncCompletion.Task.IsCompleted);
        syncCompletion.TrySetResult(Completed(SyncReason.Reconnect));
        await sync.DisposeAsync();
        await cache.DisposeAsync();
    }

    [Fact]
    public async Task RetryRealtimeAsync_AfterStart_ConnectsThenRequestsReconnectSync()
    {
        using var directory = new TemporaryDirectory();
        var realtime = new FakeRealtimeConnection
        {
            StateValue = ConnectionState.ServerUnavailable,
            StartAction = _ => Task.CompletedTask,
        };
        var sync = new FakeSyncCoordinator();
        var session = CreateSession();
        var runtime = CreateRuntime(directory.Path, session, realtime, sync);
        await runtime.StartAsync();

        var outcome = await runtime.RetryRealtimeAsync();

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(
            [SyncReason.Startup, SyncReason.Reconnect],
            sync.Reasons);
        Assert.Equal(2, realtime.StartCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task RetryRealtimeAsync_WhenRealtimeFails_StillRequestsSyncAndRedactsFailure()
    {
        using var directory = new TemporaryDirectory();
        var attempts = 0;
        var realtime = new FakeRealtimeConnection
        {
            StateValue = ConnectionState.ServerUnavailable,
            StartAction = _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return Task.CompletedTask;
                }

                throw new HttpRequestException(
                    "classified retry failure at https://private.example.test");
            },
        };
        var sync = new FakeSyncCoordinator();
        var session = CreateSession();
        var logger = new RecordingLogger<ClientAccountRuntime>();
        var runtime = CreateRuntime(directory.Path, session, realtime, sync, logger: logger);
        await runtime.StartAsync();

        var outcome = await runtime.RetryRealtimeAsync();

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(
            [SyncReason.Startup, SyncReason.Reconnect],
            sync.Reasons);
        var diagnosticText = string.Join(' ', logger.Entries);
        Assert.DoesNotContain("classified retry failure", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example.test", diagnosticText, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), diagnosticText, StringComparison.Ordinal);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenRetryIsInFlight_WaitsBeforeCacheAndSession()
    {
        using var directory = new TemporaryDirectory();
        var attempts = 0;
        var retryEntered = NewSignal();
        var releaseRetry = NewSignal();
        var terminalReachedExplicitWait = NewSignal();
        var order = new ConcurrentQueue<string>();
        var cacheDisposed = false;
        var session = CreateSession();
        var realtime = new FakeRealtimeConnection
        {
            StartAction = async _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return;
                }

                retryEntered.TrySetResult();
                await releaseRetry.Task;
            },
            DisposeAction = () =>
            {
                order.Enqueue("realtime-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var sync = new FakeSyncCoordinator
        {
            DisposeAction = () =>
            {
                order.Enqueue("sync-dispose");
                terminalReachedExplicitWait.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };
        var runtime = CreateRuntime(
            directory.Path,
            session,
            realtime,
            sync,
            new RecordingAsyncDisposable(() =>
            {
                cacheDisposed = true;
                order.Enqueue("cache-dispose");
                return ValueTask.CompletedTask;
            }));
        await runtime.StartAsync();

        var retry = runtime.RetryRealtimeAsync();
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = runtime.DisposeAsync().AsTask();
        await terminalReachedExplicitWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cacheDisposed);
        Assert.True(session.IsAuthenticated);
        Assert.False(disposal.IsCompleted);
        releaseRetry.TrySetResult();
        var retryOutcome = await retry;
        await disposal;

        Assert.Equal(ClientSyncRunStatus.Canceled, retryOutcome.Status);
        Assert.Equal(SyncReason.Reconnect, retryOutcome.Reason);
        Assert.Equal(
            ["realtime-dispose", "sync-dispose", "cache-dispose"],
            order);
        Assert.True(session.IsDisposeCompleted);
    }

    [Fact]
    public async Task FactoryRuntime_WhenConversationIsForeground_WiresActivityIntoSyncAndRealtimeTransactions()
    {
        using var directory = new TemporaryDirectory();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Foreground conversation",
            null,
            Now.AddHours(-2),
            Now.AddHours(-1),
            0,
            0,
            0);
        var syncMessage = CreateMessage(conversation.Id);
        var realtimeMessage = syncMessage with
        {
            Id = 2,
            ClientMessageId = Guid.NewGuid(),
            CreatedAt = syncMessage.CreatedAt.AddSeconds(1),
        };
        var handler = new DelegateHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/conversations",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(
                    new ConversationListResponse([conversation], Complete: true)));
            }

            Assert.EndsWith("/api/sync", request.RequestUri.AbsolutePath);
            return Task.FromResult(Ok(
                new SyncResponse([syncMessage], 1, 1, HasMore: false)));
        });
        IRealtimeEventSink? capturedSink = null;
        var factory = new ClientAccountRuntimeFactory(
            new HttpClient(handler),
            directory.Path,
            NullLoggerFactory.Instance,
            (_, _, sink, _) =>
            {
                capturedSink = sink;
                return new FakeRealtimeConnection();
            });
        var runtime = await factory.CreateAsync(CreateSession());
        runtime.UpdateActivity(new ClientActivitySnapshot(
            IsMainWindowVisible: true,
            IsMainWindowMinimized: false,
            HasForegroundFocus: true,
            OpenConversationId: conversation.Id));

        var start = await runtime.StartAsync();
        await capturedSink!.OnNewMessageAsync(realtimeMessage, CancellationToken.None);

        Assert.True(start.IsAuthoritativeCacheReady);
        using (var connection = OpenCache(runtime.Identity))
        {
            using var messages = connection.CreateCommand();
            messages.CommandText = """
                SELECT COUNT(*)
                FROM LocalMessages
                WHERE IsRead = 1 AND IsNotificationHandled = 1;
                """;
            Assert.Equal(2, Convert.ToInt32(messages.ExecuteScalar()));

            using var conversationState = connection.CreateCommand();
            conversationState.CommandText = """
                SELECT LastMessageId, LastReadMessageId,
                       PendingReadThroughMessageId, UnreadCount
                FROM LocalConversations
                WHERE Id = $conversationId;
                """;
            conversationState.Parameters.AddWithValue(
                "$conversationId",
                conversation.Id.ToString("D"));
            using var reader = conversationState.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(2, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(2, reader.GetInt64(2));
            Assert.Equal(0, reader.GetInt32(3));
        }

        await runtime.DisposeAsync();
    }

    [Fact]
    public void ActivityState_WhenWindowConditionsChange_OnlyExposesFullyForegroundConversation()
    {
        var conversationId = Guid.NewGuid();
        var state = new ClientActivityState();
        var inactiveSnapshots = new[]
        {
            new ClientActivitySnapshot(false, false, true, conversationId),
            new ClientActivitySnapshot(true, true, true, conversationId),
            new ClientActivitySnapshot(true, false, false, conversationId),
            new ClientActivitySnapshot(true, false, true, OpenConversationId: null),
        };

        Assert.Null(state.GetForegroundConversationId());
        foreach (var snapshot in inactiveSnapshots)
        {
            state.Update(snapshot);
            Assert.Null(state.GetForegroundConversationId());
        }

        var foreground = new ClientActivitySnapshot(true, false, true, conversationId);
        state.Update(foreground);

        Assert.Equal(conversationId, state.GetForegroundConversationId());
        Assert.DoesNotContain(conversationId.ToString(), foreground.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => state.Update(
            new ClientActivitySnapshot(true, false, true, Guid.Empty)));
    }

    [Fact]
    public async Task UpdateActivity_AfterRuntimeDisposal_RejectsLateWindowState()
    {
        using var directory = new TemporaryDirectory();
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator());
        await runtime.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => runtime.UpdateActivity(
            new ClientActivitySnapshot(true, false, true, Guid.NewGuid())));
    }

    [Fact]
    public async Task DisposeAsync_WhenExplicitSyncIsInFlight_WaitsBeforeCacheAndSession()
    {
        using var directory = new TemporaryDirectory();
        var syncEntered = NewSignal();
        var releaseSync = NewSignal();
        var coordinatorDisposeCalled = NewSignal();
        var cacheDisposed = false;
        var session = CreateSession();
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = async (reason, _) =>
            {
                if (reason == SyncReason.Startup)
                {
                    return Completed(reason);
                }

                syncEntered.TrySetResult();
                await releaseSync.Task;
                return Completed(reason);
            },
            DisposeAction = () =>
            {
                coordinatorDisposeCalled.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };
        var runtime = CreateRuntime(
            directory.Path,
            session,
            new FakeRealtimeConnection(),
            sync,
            new RecordingAsyncDisposable(() =>
            {
                cacheDisposed = true;
                return ValueTask.CompletedTask;
            }));
        await runtime.StartAsync();

        var explicitSync = runtime.TriggerSyncAsync(SyncReason.WindowActivated);
        await syncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = runtime.DisposeAsync().AsTask();
        await coordinatorDisposeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cacheDisposed);
        Assert.True(session.IsAuthenticated);
        Assert.False(disposal.IsCompleted);
        releaseSync.TrySetResult();
        var syncOutcome = await explicitSync;
        await disposal;

        Assert.Equal(ClientSyncRunStatus.Completed, syncOutcome.Status);
        Assert.True(cacheDisposed);
        Assert.True(session.IsDisposeCompleted);
    }

    [Fact]
    public async Task DisposeAsync_WhenRuntimeIsActive_StopsProducersBeforeCacheAndSession()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var session = CreateSession();
        var realtime = new FakeRealtimeConnection
        {
            DisposeAction = () =>
            {
                Assert.True(session.IsAuthenticated);
                order.Enqueue("realtime-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var sync = new FakeSyncCoordinator
        {
            DisposeAction = () =>
            {
                Assert.True(session.IsAuthenticated);
                order.Enqueue("sync-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var cache = new RecordingAsyncDisposable(() =>
        {
            Assert.True(session.IsAuthenticated);
            order.Enqueue("cache-dispose");
            return ValueTask.CompletedTask;
        });
        var readThrough = new FakeReadThroughCoordinator
        {
            DisposeAction = () =>
            {
                Assert.True(session.IsAuthenticated);
                order.Enqueue("read-through-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var notificationCoordinator = new RecordingAsyncDisposable(() =>
        {
            Assert.True(session.IsAuthenticated);
            order.Enqueue("notification-dispose");
            return ValueTask.CompletedTask;
        });
        var runtime = CreateRuntime(
            directory.Path,
            session,
            realtime,
            sync,
            cache,
            readThrough,
            notificationCoordinator: notificationCoordinator);
        await runtime.StartAsync();

        await Task.WhenAll(
            runtime.DisposeAsync().AsTask(),
            runtime.DisposeAsync().AsTask());

        Assert.Equal(
            [
                "realtime-dispose",
                "sync-dispose",
                "read-through-dispose",
                "notification-dispose",
                "cache-dispose",
            ],
            order);
        Assert.True(session.IsDisposeCompleted);
        Assert.False(session.IsAuthenticated);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.StartAsync());
    }

    [Fact]
    public async Task DisposeAsync_WhenStartupIsInFlight_WaitsForCanceledStartBeforeCache()
    {
        using var directory = new TemporaryDirectory();
        var startEntered = NewSignal();
        var releaseStart = NewSignal();
        var terminalReachedStartupWait = NewSignal();
        var cacheDisposed = false;
        var session = CreateSession();
        var realtime = new FakeRealtimeConnection
        {
            StartAction = async _ =>
            {
                startEntered.TrySetResult();
                await releaseStart.Task;
            },
        };
        var sync = new FakeSyncCoordinator
        {
            DisposeAction = () =>
            {
                terminalReachedStartupWait.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };
        var runtime = CreateRuntime(
            directory.Path,
            session,
            realtime,
            sync,
            new RecordingAsyncDisposable(() =>
            {
                cacheDisposed = true;
                return ValueTask.CompletedTask;
            }));

        var startup = runtime.StartAsync();
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = runtime.DisposeAsync().AsTask();
        await terminalReachedStartupWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cacheDisposed);
        Assert.True(session.IsAuthenticated);
        Assert.False(disposal.IsCompleted);
        releaseStart.TrySetResult();
        var startOutcome = await startup;
        await disposal;

        Assert.Equal(ClientSyncRunStatus.Canceled, startOutcome.StartupSyncOutcome.Status);
        Assert.True(cacheDisposed);
        Assert.True(session.IsDisposeCompleted);
    }

    [Fact]
    public async Task LogoutAsync_WhenWaiterCancels_CompletesCleanupAndRemoteLogoutInOrder()
    {
        using var directory = new TemporaryDirectory();
        var order = new ConcurrentQueue<string>();
        var remoteLogoutCalled = false;
        var session = CreateSession(new DelegateHttpHandler((request, _) =>
        {
            Assert.EndsWith("/api/auth/logout", request.RequestUri!.AbsolutePath);
            Assert.Equal(
                ["realtime-dispose", "sync-dispose", "cache-dispose"],
                order);
            remoteLogoutCalled = true;
            order.Enqueue("session-logout");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var realtimeDisposeEntered = NewSignal();
        var releaseRealtimeDispose = NewSignal();
        var realtime = new FakeRealtimeConnection
        {
            DisposeAction = async () =>
            {
                order.Enqueue("realtime-dispose");
                realtimeDisposeEntered.TrySetResult();
                await releaseRealtimeDispose.Task;
            },
        };
        var sync = new FakeSyncCoordinator
        {
            DisposeAction = () =>
            {
                order.Enqueue("sync-dispose");
                return ValueTask.CompletedTask;
            },
        };
        var cache = new RecordingAsyncDisposable(() =>
        {
            order.Enqueue("cache-dispose");
            return ValueTask.CompletedTask;
        });
        var runtime = CreateRuntime(directory.Path, session, realtime, sync, cache);
        using var callerCancellation = new CancellationTokenSource();

        var canceledWaiter = runtime.LogoutAsync(callerCancellation.Token);
        await realtimeDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        releaseRealtimeDispose.TrySetResult();
        var status = await runtime.LogoutAsync();

        Assert.Equal(ClientLogoutStatus.LoggedOut, status);
        Assert.True(remoteLogoutCalled);
        Assert.Equal(
            [
                "realtime-dispose",
                "sync-dispose",
                "cache-dispose",
                "session-logout",
            ],
            order);
        Assert.True(session.IsDisposeCompleted);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenCallerIsAlreadyCanceled_StillCompletesCleanup()
    {
        using var directory = new TemporaryDirectory();
        var realtimeDisposeEntered = NewSignal();
        var releaseRealtimeDispose = NewSignal();
        var session = CreateSession();
        var runtime = CreateRuntime(
            directory.Path,
            session,
            new FakeRealtimeConnection
            {
                DisposeAction = async () =>
                {
                    realtimeDisposeEntered.TrySetResult();
                    await releaseRealtimeDispose.Task;
                },
            },
            new FakeSyncCoordinator());
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        var canceledWaiter = runtime.LogoutAsync(callerCancellation.Token);
        await realtimeDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        releaseRealtimeDispose.TrySetResult();

        Assert.Equal(ClientLogoutStatus.LoggedOut, await runtime.LogoutAsync());
        Assert.True(session.IsDisposeCompleted);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_BeforeAccountSwitch_ReleasesPersistentSessionLast()
    {
        using var directory = new TemporaryDirectory();
        var store = new ClientCredentialStore(
            directory.Path,
            NullLogger<ClientCredentialStore>.Instance);
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : new HttpResponseMessage(HttpStatusCode.NoContent)));
        var authentication = new PersistentClientAuthentication(
            new HttpClient(handler),
            store,
            NullLogger<ClientAuthenticationClient>.Instance,
            NullLogger<PersistentClientAuthentication>.Instance,
            new FixedTimeProvider(Now));
        var first = await authentication.LoginAsync(
            ServerBaseUri,
            new LoginRequest("runtime-user", "runtime-password", "test-device", "1.0.0"));
        var order = new ConcurrentQueue<string>();
        var runtime = CreateRuntime(
            directory.Path,
            first.Session!,
            new FakeRealtimeConnection
            {
                DisposeAction = () =>
                {
                    order.Enqueue("realtime-dispose");
                    return ValueTask.CompletedTask;
                },
            },
            new FakeSyncCoordinator
            {
                DisposeAction = () =>
                {
                    order.Enqueue("sync-dispose");
                    return ValueTask.CompletedTask;
                },
            },
            new RecordingAsyncDisposable(() =>
            {
                order.Enqueue("cache-dispose");
                return ValueTask.CompletedTask;
            }));

        var rejected = await authentication.LoginAsync(
            ServerBaseUri,
            new LoginRequest("runtime-user", "runtime-password", "test-device", "1.0.0"));
        Assert.Equal(PersistentClientAuthenticationStatus.SessionAlreadyActive, rejected.Status);

        await runtime.DisposeAsync();
        var retained = await store.LoadAsync();
        Assert.Equal(ClientCredentialReadStatus.Loaded, retained.Status);
        Assert.Equal(RefreshToken, retained.Credential!.RefreshToken);
        var switched = await authentication.LoginAsync(
            ServerBaseUri,
            new LoginRequest("runtime-user", "runtime-password", "test-device", "1.0.0"));

        Assert.Equal(PersistentClientAuthenticationStatus.Authenticated, switched.Status);
        Assert.Equal(
            ["realtime-dispose", "sync-dispose", "cache-dispose"],
            order);
        Assert.Equal(2, handler.RequestCountFor("/login"));
        await switched.Session!.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenSessionIsPersistent_ClearsCredentialAndRevokesRemotely()
    {
        using var directory = new TemporaryDirectory();
        var store = new ClientCredentialStore(
            directory.Path,
            NullLogger<ClientCredentialStore>.Instance);
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : new HttpResponseMessage(HttpStatusCode.NoContent)));
        var authentication = new PersistentClientAuthentication(
            new HttpClient(handler),
            store,
            NullLogger<ClientAuthenticationClient>.Instance,
            NullLogger<PersistentClientAuthentication>.Instance,
            new FixedTimeProvider(Now));
        var login = await authentication.LoginAsync(
            ServerBaseUri,
            new LoginRequest("runtime-user", "runtime-password", "test-device", "1.0.0"));
        var runtime = CreateRuntime(
            directory.Path,
            login.Session!,
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator());

        var status = await runtime.LogoutAsync();

        Assert.Equal(ClientLogoutStatus.LoggedOut, status);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await store.LoadAsync()).Status);
        Assert.Equal(1, handler.RequestCountFor("/logout"));
        Assert.True(login.Session!.IsDisposeCompleted);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenResourceCleanupFails_StillDisposesCacheAndSession()
    {
        using var directory = new TemporaryDirectory();
        var cacheDisposed = false;
        var session = CreateSession();
        var runtime = CreateRuntime(
            directory.Path,
            session,
            new FakeRealtimeConnection
            {
                DisposeAction = () =>
                    throw new IOException("classified realtime cleanup detail"),
            },
            new FakeSyncCoordinator
            {
                DisposeAction = () => ValueTask.FromException(
                    new InvalidOperationException("classified sync cleanup detail")),
            },
            new RecordingAsyncDisposable(() =>
            {
                cacheDisposed = true;
                return ValueTask.CompletedTask;
            }),
            readThrough: new FakeReadThroughCoordinator
            {
                DisposeAction = () => ValueTask.FromException(
                    new ApplicationException("classified read-through cleanup detail")),
            });

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            runtime.DisposeAsync().AsTask());

        Assert.Equal(3, exception.InnerExceptions.Count);
        Assert.True(cacheDisposed);
        Assert.True(session.IsDisposeCompleted);
    }

    [Fact]
    public async Task TryAuthorizeNotificationTarget_WhenScopeMatches_UsesRuntimeOwnedAuthorizer()
    {
        using var directory = new TemporaryDirectory();
        var authorizerCalls = 0;
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            notificationTargetAuthorizer: target =>
            {
                authorizerCalls++;
                return target.Kind == ClientNotificationActivationKind.UnreadOverview;
            });
        var target = ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id);

        var result = runtime.TryAuthorizeNotificationTarget(target);

        Assert.True(result);
        Assert.Equal(1, authorizerCalls);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task TryAuthorizeNotificationTarget_WhenScopeDiffersOrStopping_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var authorizerCalls = 0;
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            notificationTargetAuthorizer: _ =>
            {
                authorizerCalls++;
                return true;
            });
        var differentScope = AccountScopeIdentity.Create(
            new Uri("https://different.example/"),
            UserId,
            directory.Path).Id;

        Assert.False(runtime.TryAuthorizeNotificationTarget(
            ClientNotificationActivationTarget.UnreadOverview(differentScope)));
        await runtime.DisposeAsync();
        Assert.False(runtime.TryAuthorizeNotificationTarget(
            ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id)));
        Assert.Equal(0, authorizerCalls);
    }

    [Fact]
    public async Task TryAuthorizeNotificationTarget_WhenAuthenticationSessionIsCleared_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var authorizerCalls = 0;
        var session = CreateSession();
        var runtime = CreateRuntime(
            directory.Path,
            session,
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            notificationTargetAuthorizer: _ =>
            {
                authorizerCalls++;
                return true;
            });
        var target = ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id);

        Assert.Equal(ClientLogoutStatus.LoggedOut, await session.LogoutAsync());

        Assert.False(runtime.TryAuthorizeNotificationTarget(target));
        Assert.Equal(0, authorizerCalls);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task TryAuthorizeNotificationTarget_WhenAuthorizerFails_LogsOnlyErrorType()
    {
        using var directory = new TemporaryDirectory();
        var logger = new RecordingLogger<ClientAccountRuntime>();
        var runtime = CreateRuntime(
            directory.Path,
            CreateSession(),
            new FakeRealtimeConnection(),
            new FakeSyncCoordinator(),
            logger: logger,
            notificationTargetAuthorizer: _ =>
                throw new InvalidOperationException("classified authorization detail"));

        var result = runtime.TryAuthorizeNotificationTarget(
            ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id));

        Assert.False(result);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("classified authorization detail", StringComparison.Ordinal));
        await runtime.DisposeAsync();
    }

    private static ClientAccountRuntime CreateRuntime(
        string rootDirectory,
        ClientAuthenticationSession session,
        FakeRealtimeConnection realtime,
        FakeSyncCoordinator sync,
        IAsyncDisposable? cache = null,
        FakeReadThroughCoordinator? readThrough = null,
        ILogger<ClientAccountRuntime>? logger = null,
        IAsyncDisposable? notificationCoordinator = null,
        Func<ClientNotificationActivationTarget, bool>? notificationTargetAuthorizer = null,
        ClientAutomaticSyncScheduler? automaticSyncScheduler = null,
        FakeAttachmentDownloadCoordinator? attachmentDownloadCoordinator = null) =>
        new(
            AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory),
            session,
            realtime,
            sync,
            readThrough ?? new FakeReadThroughCoordinator(),
            notificationCoordinator,
            cache ?? new RecordingAsyncDisposable(() => ValueTask.CompletedTask),
            new ClientActivityState(),
            logger ?? NullLogger<ClientAccountRuntime>.Instance,
            automaticSyncScheduler ??
                new ClientAutomaticSyncScheduler(
                    sync,
                    NullLogger<ClientAutomaticSyncScheduler>.Instance),
            notificationTargetAuthorizer,
            attachmentDownloadCoordinator: attachmentDownloadCoordinator);

    private static ClientAuthenticationSession CreateSession(
        HttpMessageHandler? handler = null) =>
        new(
            ServerBaseUri,
            new HttpClient(handler ?? new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)))),
            NullLogger<ClientAuthenticationClient>.Instance,
            CreateLoginResponse(),
            new FixedTimeProvider(Now));

    private static LoginResponse CreateLoginResponse() =>
        new(
            UserId,
            "Runtime User",
            AccessToken,
            RefreshToken,
            Now.AddHours(1),
            "1.0.0",
            "1.0.0");

    private static HttpResponseMessage Ok<T>(T response) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response),
        };

    private static ClientSyncRunOutcome Completed(SyncReason reason) =>
        new(ClientSyncRunStatus.Completed, reason, RoundsExecuted: 1);

    private static MessageDto CreateMessage(Guid conversationId) =>
        new(
            1,
            Guid.Parse("985191ff-9e5f-4ac2-86ed-8489d47250ca"),
            conversationId,
            Guid.Parse("2aba633b-e9a0-4c95-a6f4-8f13a369857f"),
            "Unknown Sender",
            MessageType.Text,
            "classified message body",
            null,
            [],
            [],
            Now);

    private static SqliteConnection OpenCache(AccountScopeIdentity identity)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenCache(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task WaitForClockCancellationAsync(
        CancellationToken cancellationToken,
        ConcurrentQueue<string> order)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            order.Enqueue("automatic-clock-canceled");
            throw;
        }
    }

    private sealed class FakeRealtimeConnection : IClientAccountRealtimeConnection
    {
        private int startCount;

        public Func<CancellationToken, Task>? StartAction { get; init; }

        public Func<ValueTask>? DisposeAction { get; init; }

        public ConnectionState StateValue { get; set; } = ConnectionState.Connected;

        public ConnectionState State => StateValue;

        public int StartCount => Volatile.Read(ref startCount);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref startCount);
            return StartAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync() =>
            DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
    }

    private sealed class FakeSyncCoordinator : IClientAccountSyncCoordinator
    {
        private readonly ConcurrentQueue<SyncReason> reasons = new();

        public Func<SyncReason, CancellationToken, Task<ClientSyncRunOutcome>>? TriggerAction
        {
            get;
            init;
        }

        public Func<ValueTask>? DisposeAction { get; init; }

        public IReadOnlyList<SyncReason> Reasons => reasons.ToArray();

        public Task<ClientSyncRunOutcome> TriggerAsync(
            SyncReason reason,
            CancellationToken cancellationToken = default)
        {
            reasons.Enqueue(reason);
            return TriggerAction?.Invoke(reason, cancellationToken) ??
                Task.FromResult(Completed(reason));
        }

        public ValueTask DisposeAsync() =>
            DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
    }

    private sealed class RecordingNotificationPlatform : IClientNotificationPlatform
    {
        private readonly ConcurrentQueue<ClientNotificationRequest> requests = new();

        public IReadOnlyCollection<ClientNotificationRequest> Requests => requests.ToArray();

        public Task<ClientNotificationPlatformResult> SubmitAsync(
            ClientNotificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            return Task.FromResult(ClientNotificationPlatformResult.Accepted);
        }

        public Task<ClientNotificationPlatformResult> ClearConversationAsync(
            string accountScopeId,
            Guid conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ClientNotificationPlatformResult.Accepted);

        public Task<ClientNotificationPlatformResult> ClearSummaryAsync(
            string accountScopeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ClientNotificationPlatformResult.Accepted);
    }

    private sealed class FakeReadThroughCoordinator : IClientAccountReadThroughCoordinator
    {
        public Func<CancellationToken, Task<ClientReadThroughRunOutcome>>? TriggerAction
        {
            get;
            init;
        }

        public Func<ValueTask>? DisposeAction { get; init; }

        public Task<ClientReadThroughRunOutcome> TriggerAsync(
            CancellationToken cancellationToken = default) =>
            TriggerAction?.Invoke(cancellationToken) ??
            Task.FromResult(new ClientReadThroughRunOutcome(
                ClientReadThroughRunStatus.Completed,
                RequestCount: 0,
                ReceiptCount: 0));

        public ValueTask DisposeAsync() =>
            DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
    }

    private sealed class FakeAttachmentDownloadCoordinator : IClientAttachmentDownloadCoordinator
    {
        public Func<Guid, Guid, ClientAttachmentRevealCommit, CancellationToken,
            Task<ClientAttachmentRevealOutcome>>?
            RevealAction
        {
            get;
            init;
        }

        public Func<Guid, Guid, ClientAttachmentImageRendition,
            ClientAttachmentImageCommit, CancellationToken,
            Task<ClientAttachmentImageLoadOutcome>>?
            ImageLoadAction
        {
            get;
            init;
        }

        public bool IsDisposeCompleted { get; private set; }

        public Task<ClientAttachmentCacheRecoveryStatus> RecoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientAttachmentCacheRecoveryStatus.Ready);

        public Task<ClientAttachmentDownloadOutcome> DownloadAsync(
            Guid conversationId,
            Guid attachmentId,
            CancellationToken cancellationToken = default,
            IProgress<ClientAttachmentDownloadProgress>? progress = null) =>
            Task.FromResult(ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure));

        public Task<ClientAttachmentRevealOutcome> RevealInFolderAsync(
            Guid conversationId,
            Guid attachmentId,
            ClientAttachmentRevealCommit commit,
            CancellationToken cancellationToken = default) =>
            RevealAction?.Invoke(conversationId, attachmentId, commit, cancellationToken) ??
            Task.FromResult(ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.LocalCacheFailure));

        public Task<ClientAttachmentImageLoadOutcome> LoadImageAsync(
            Guid conversationId,
            Guid attachmentId,
            ClientAttachmentImageRendition rendition,
            ClientAttachmentImageCommit commit,
            CancellationToken cancellationToken = default) =>
            ImageLoadAction?.Invoke(
                conversationId,
                attachmentId,
                rendition,
                commit,
                cancellationToken) ??
            Task.FromResult(ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.LocalCacheFailure));

        public ValueTask DisposeAsync()
        {
            IsDisposeCompleted = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAsyncDisposable(Func<ValueTask> disposeAsync) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => disposeAsync();
    }

    private sealed class RecordingRealtimeSink : IRealtimeEventSink
    {
        private readonly ConcurrentQueue<ConnectionState> states = new();

        public IReadOnlyList<ConnectionState> States => states.ToArray();

        public Task OnConnectionStateChangedAsync(
            ConnectionState state,
            CancellationToken cancellationToken)
        {
            states.Enqueue(state);
            return Task.CompletedTask;
        }

        public Task OnNewMessageAsync(
            MessageDto message,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnConversationAccessRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWindowsAttachmentShell : IWindowsAttachmentShell
    {
        public int RevealCount { get; private set; }

        public WindowsAttachmentShellStatus Reveal(
            ClientAttachmentCacheStore.ValidatedFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            RevealCount++;
            return WindowsAttachmentShellStatus.Revealed;
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) :
        HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> requestPaths = new();

        public int RequestCountFor(string suffix) =>
            requestPaths.Count(path => path.EndsWith(suffix, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestPaths.Enqueue(request.RequestUri!.AbsolutePath);
            return sendAsync(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            var testRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RelayCove.AccountRuntime.Tests"));
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                testRoot,
                Guid.NewGuid().ToString("N")));
            var relativePath = System.IO.Path.GetRelativePath(testRoot, Path);
            if (System.IO.Path.IsPathFullyQualified(relativePath) ||
                relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Test directory escaped its root.");
            }

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
