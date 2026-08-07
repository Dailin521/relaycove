using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Admin;
using RelayCove.Client.Auth;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Users;

namespace RelayCove.Client.Tests.Admin;

public sealed class ClientAdminCoordinatorTests
{
    private static readonly Guid UserId = Guid.Parse("155eab33-eed3-4492-98c1-7be9d6ff75cc");

    [Fact]
    public async Task ProbeAsync_WhenMeReturnsMatchingAdministrator_EnablesAdminState()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Assert.Equal("/api/auth/me", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer access-one", request.Headers.Authorization!.ToString());
            return Task.FromResult(Json(HttpStatusCode.OK,
                new CurrentUserResponse(UserId, "admin", "Administrator", true)));
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);

        var isAdmin = await coordinator.ProbeAsync();

        Assert.True(isAdmin);
        Assert.True(coordinator.Snapshot.IsAdmin);
    }

    [Fact]
    public async Task ProbeAsync_WhenUnauthorizedAfterOneRefresh_RaisesAuthenticationRequired()
    {
        var meCalls = 0;
        var refreshCalls = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                meCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            Assert.Equal("/api/auth/refresh", request.RequestUri.AbsolutePath);
            refreshCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);
        var authenticationRequired = 0;
        coordinator.AuthenticationRequired += () =>
        {
            Interlocked.Increment(ref authenticationRequired);
            return Task.CompletedTask;
        };

        var isAdmin = await coordinator.ProbeAsync();

        Assert.False(isAdmin);
        Assert.False(coordinator.Snapshot.IsAdmin);
        Assert.Equal(1, meCalls);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(1, Volatile.Read(ref authenticationRequired));
    }

    [Fact]
    public async Task RefreshAsync_WhenAdminEndpointReturnsForbidden_HidesAdminState()
    {
        var meCalls = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                meCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK,
                    new CurrentUserResponse(UserId, "admin", "Administrator", true)));
            }

            Assert.Equal("/api/admin/users", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);
        Assert.True(await coordinator.ProbeAsync());

        var status = await coordinator.RefreshAsync();

        Assert.Equal(ClientAdminRequestStatus.AccessDenied, status);
        Assert.False(coordinator.Snapshot.IsAdmin);
        Assert.Equal(1, meCalls);
    }

    [Fact]
    public async Task RefreshAsync_WhenDisposedDuringSlowRequest_DoesNotRepublishOldAdminSnapshot()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    new CurrentUserResponse(UserId, "admin", "Administrator", true)));
            }

            Assert.Equal("/api/admin/users", request.RequestUri.AbsolutePath);
            requestStarted.TrySetResult();
            return response.Task;
        }));
        await using var session = CreateSession(client);
        var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);
        Assert.True(await coordinator.ProbeAsync());

        var refresh = coordinator.RefreshAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.DisposeAsync();
        response.TrySetResult(Json(HttpStatusCode.OK, Array.Empty<AdminUserResponse>()));

        Assert.Equal(ClientAdminRequestStatus.Canceled, await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ClientAdminSnapshot.Hidden, coordinator.Snapshot);
    }

    [Fact]
    public async Task ChatOperations_WhenCurrentUserIsNotGlobalAdmin_RemainAvailable()
    {
        var conversationId = Guid.Parse("80e11ae9-c823-4fdc-93c1-4081c0925de6");
        var teammateId = Guid.Parse("7b4d79fb-9dca-40cd-a12c-275265f3fe0c");
        var directoryEntry = new UserDirectoryEntryDto(teammateId, "teammate", "Team Mate");
        var conversation = new ConversationDto(
            conversationId,
            ConversationType.PrivateChannel,
            "Private",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            0,
            0);
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/me" => Task.FromResult(Json(
                    HttpStatusCode.OK,
                    new CurrentUserResponse(UserId, "normal", "Normal", false))),
                "/api/users" => Task.FromResult(Json(
                    HttpStatusCode.OK,
                    new[] { directoryEntry })),
                "/api/conversations" when request.Method == HttpMethod.Post =>
                    Task.FromResult(Json(HttpStatusCode.Created, conversation)),
                var path when path == $"/api/conversations/{conversationId:D}/participants" =>
                    Task.FromResult(Json(
                        HttpStatusCode.OK,
                        new ConversationParticipantListResponse(
                            conversationId,
                            ConversationType.PrivateChannel,
                            CanManageMembers: true,
                            [directoryEntry]))),
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {request.Method} {request.RequestUri.AbsolutePath}"),
            };
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client,
            session,
            NullLogger<ClientAdminCoordinator>.Instance);
        Assert.False(await coordinator.ProbeAsync());

        var directory = await coordinator.GetUserDirectoryAsync();
        var created = await coordinator.CreateConversationForChatAsync(
            new CreateConversationRequest(ConversationType.PrivateChannel, "Private"));
        var participants = await coordinator.GetConversationParticipantsAsync(conversationId);

        Assert.Equal(ClientAdminRequestStatus.Completed, directory.Status);
        Assert.Equal(directoryEntry, Assert.Single(directory.Value!));
        Assert.Equal(ClientAdminRequestStatus.Completed, created.Status);
        Assert.Equal(conversationId, created.Value!.Id);
        Assert.Equal(ClientAdminRequestStatus.Completed, participants.Status);
        Assert.True(participants.Value!.CanManageMembers);
        Assert.False(coordinator.Snapshot.IsAdmin);
    }

    [Fact]
    public async Task LoadPrivateMembersAsync_WhenAuthorized_PublishesObservableRoster()
    {
        var channelId = Guid.Parse("962c63f6-f2f6-46a3-a419-59702ea74f45");
        var member = new ConversationMemberDto(
            Guid.Parse("629ad2f9-ff07-4295-8bf4-c71ff11d5135"),
            "member",
            "Team Member",
            ConversationMemberRole.Member,
            DateTimeOffset.UtcNow,
            0,
            false);
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    new CurrentUserResponse(UserId, "admin", "Administrator", true)));
            }

            Assert.Equal($"/api/conversations/{channelId:D}/members", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json(HttpStatusCode.OK, new[] { member }));
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);
        Assert.True(await coordinator.ProbeAsync());

        var status = await coordinator.LoadPrivateMembersAsync(channelId);

        Assert.Equal(ClientAdminRequestStatus.Completed, status);
        Assert.Equal(channelId, coordinator.Snapshot.SelectedPrivateChannelId);
        Assert.Equal(member, Assert.Single(coordinator.Snapshot.PrivateMembers));
    }

    [Fact]
    public async Task LoadPrivateMembersAsync_WhenSelectionChanges_CancelsOldRosterBeforePublishingNewRoster()
    {
        var firstChannelId = Guid.Parse("cd39ec7f-5fd0-41ff-b903-64e6b780531e");
        var secondChannelId = Guid.Parse("17ba11c2-3f30-4530-b3be-6335e3b4f559");
        var secondMember = new ConversationMemberDto(
            Guid.Parse("236623d5-f392-4201-b64e-d1d167a521ec"),
            "second-member",
            "Second Member",
            ConversationMemberRole.Member,
            DateTimeOffset.UtcNow,
            0,
            false);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/me")
            {
                return Json(HttpStatusCode.OK,
                    new CurrentUserResponse(UserId, "admin", "Administrator", true));
            }

            if (request.RequestUri.AbsolutePath == $"/api/conversations/{firstChannelId:D}/members")
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Assert.Equal(
                $"/api/conversations/{secondChannelId:D}/members",
                request.RequestUri.AbsolutePath);
            return Json(HttpStatusCode.OK, new[] { secondMember });
        }));
        await using var session = CreateSession(client);
        await using var coordinator = new ClientAdminCoordinator(
            client, session, NullLogger<ClientAdminCoordinator>.Instance);
        Assert.True(await coordinator.ProbeAsync());
        using var firstCancellation = new CancellationTokenSource();

        var firstLoad = coordinator.LoadPrivateMembersAsync(firstChannelId, firstCancellation.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondLoad = coordinator.LoadPrivateMembersAsync(secondChannelId);
        firstCancellation.Cancel();

        Assert.Equal(ClientAdminRequestStatus.Canceled, await firstLoad.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ClientAdminRequestStatus.Completed, await secondLoad.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(secondChannelId, coordinator.Snapshot.SelectedPrivateChannelId);
        Assert.Equal(secondMember, Assert.Single(coordinator.Snapshot.PrivateMembers));
    }

    private static ClientAuthenticationSession CreateSession(HttpClient client) =>
        new(
            new Uri("https://relay.example/"),
            client,
            NullLogger.Instance,
            new LoginResponse(
                UserId,
                "Administrator",
                "access-one",
                "refresh-one",
                DateTimeOffset.UtcNow.AddHours(1),
                "1.0.0",
                "1.0.0"),
            TimeProvider.System);

    private static HttpResponseMessage Json<T>(HttpStatusCode statusCode, T value) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
