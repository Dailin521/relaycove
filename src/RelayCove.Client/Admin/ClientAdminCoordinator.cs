using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Users;

namespace RelayCove.Client.Admin;

internal sealed class ClientAdminCoordinator : IAsyncDisposable
{
    private readonly object stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly ClientAdminTransport transport;
    private readonly Guid userId;
    private ClientAdminSnapshot snapshot = ClientAdminSnapshot.Hidden;
    private int disposed;

    public ClientAdminCoordinator(
        HttpClient httpClient,
        ClientAuthenticationSession session,
        ILogger<ClientAdminCoordinator> logger)
    {
        userId = session?.UserId ?? throw new ArgumentException(
            "An authenticated session is required.", nameof(session));
        transport = new ClientAdminTransport(httpClient, session, logger);
    }

    public event Action<ClientAdminSnapshot>? SnapshotChanged;

    public event Func<Task>? AuthenticationRequired;

    public ClientAdminSnapshot Snapshot => Volatile.Read(ref snapshot);

    public Guid CurrentUserId => userId;

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        ClientAdminRequestResult<CurrentUserResponse> result;
        try
        {
            result = await transport.GetAsync<CurrentUserResponse>(
                    "api/auth/me",
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        if (result.Status == ClientAdminRequestStatus.AuthenticationRequired)
        {
            await NotifyAuthenticationRequiredAsync().ConfigureAwait(false);
            return false;
        }

        Publish(result.Status == ClientAdminRequestStatus.Completed &&
            result.Value is { IsAdmin: true } currentUser && currentUser.UserId == userId
            ? ClientAdminSnapshot.Hidden with { IsAdmin = true }
            : ClientAdminSnapshot.Hidden);
        return Snapshot.IsAdmin;
    }

    public Task<ClientAdminRequestStatus> RefreshAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var users = await transport.GetAsync<IReadOnlyList<AdminUserResponse>>("api/admin/users", token)
                .ConfigureAwait(false);
            if (users.Status != ClientAdminRequestStatus.Completed)
            {
                return users.Status;
            }

            var channels = await transport.GetAsync<IReadOnlyList<AdminChannelResponse>>("api/admin/channels", token)
                .ConfigureAwait(false);
            if (channels.Status != ClientAdminRequestStatus.Completed)
            {
                return channels.Status;
            }

            var status = await transport.GetAsync<ServerStatusResponse>("api/admin/status", token)
                .ConfigureAwait(false);
            if (status.Status != ClientAdminRequestStatus.Completed)
            {
                return status.Status;
            }

            var settings = await transport.GetAsync<UploadSettingsResponse>("api/admin/settings/upload", token)
                .ConfigureAwait(false);
            if (settings.Status == ClientAdminRequestStatus.Completed)
            {
                Publish(new ClientAdminSnapshot(true, false, ClientAdminRequestStatus.Completed,
                    users.Value!, channels.Value!, status.Value, settings.Value,
                    null, Array.Empty<ConversationMemberDto>()));
            }

            return settings.Status;
        }, cancellationToken);

    public Task<ClientAdminRequestStatus> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Post, "api/admin/users", request, cancellationToken);

    public Task<ClientAdminRequestStatus> SetUserDisabledAsync(Guid userId, bool isDisabled, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Put, $"api/admin/users/{userId:D}", new UpdateAdminUserRequest(isDisabled), cancellationToken);

    public Task<ClientAdminRequestStatus> ResetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Post, $"api/admin/users/{userId:D}/reset-password", new ResetUserPasswordRequest(password), cancellationToken);

    public Task<ClientAdminRequestStatus> RetireUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync<object>(HttpMethod.Delete, $"api/admin/users/{userId:D}", null, cancellationToken);

    public Task<ClientAdminRequestStatus> CreateChannelAsync(CreateConversationRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Post, "api/conversations", request, cancellationToken);

    public Task<ClientAdminRequestStatus> RenameChannelAsync(Guid channelId, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Put, $"api/conversations/{channelId:D}", new UpdateConversationRequest(name), cancellationToken);

    public Task<ClientAdminRequestStatus> DeleteChannelAsync(Guid channelId, CancellationToken cancellationToken = default) =>
        MutateAsync<object>(HttpMethod.Delete, $"api/conversations/{channelId:D}", null, cancellationToken);

    public Task<ClientAdminRequestStatus> AddPrivateMemberAsync(Guid channelId, UpsertConversationMemberRequest request, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Post, $"api/conversations/{channelId:D}/members", request, cancellationToken);

    public Task<ClientAdminRequestStatus> RemovePrivateMemberAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) =>
        MutateAsync<object>(HttpMethod.Delete, $"api/conversations/{channelId:D}/members/{userId:D}", null, cancellationToken);

    public Task<ClientAdminRequestStatus> SaveUploadLimitAsync(long maximumFileBytes, CancellationToken cancellationToken = default) =>
        MutateAsync(HttpMethod.Put, "api/admin/settings/upload", new UpdateUploadSettingsRequest(maximumFileBytes), cancellationToken);

    public Task<ClientAdminRequestResult<ConversationDto>> CreateConversationForChatAsync(
        CreateConversationRequest request,
        CancellationToken cancellationToken = default) =>
        RunAuthenticatedAsync(
            token => transport.SendAsync<CreateConversationRequest, ConversationDto>(
                HttpMethod.Post,
                "api/conversations",
                request,
                token),
            cancellationToken);

    public Task<ClientAdminRequestResult<IReadOnlyList<UserDirectoryEntryDto>>> GetUserDirectoryAsync(
        CancellationToken cancellationToken = default) =>
        RunAuthenticatedAsync(
            token => transport.GetAsync<IReadOnlyList<UserDirectoryEntryDto>>(
                "api/users",
                token),
            cancellationToken);

    public Task<ClientAdminRequestResult<ConversationParticipantListResponse>> GetConversationParticipantsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        conversationId == Guid.Empty
            ? Task.FromResult(ClientAdminRequestResult<ConversationParticipantListResponse>.Failure(
                ClientAdminRequestStatus.ValidationFailed))
            : RunAuthenticatedAsync(
                token => transport.GetAsync<ConversationParticipantListResponse>(
                    $"api/conversations/{conversationId:D}/participants",
                    token),
                cancellationToken);

    public Task<ClientAdminRequestResult<ConversationMemberDto>> UpsertConversationMemberForChatAsync(
        Guid conversationId,
        UpsertConversationMemberRequest request,
        CancellationToken cancellationToken = default) =>
        conversationId == Guid.Empty
            ? Task.FromResult(ClientAdminRequestResult<ConversationMemberDto>.Failure(
                ClientAdminRequestStatus.ValidationFailed))
            : RunAuthenticatedAsync(
                token => transport.SendAsync<UpsertConversationMemberRequest, ConversationMemberDto>(
                    HttpMethod.Post,
                    $"api/conversations/{conversationId:D}/members",
                    request,
                    token),
                cancellationToken);

    public Task<ClientAdminRequestResult<bool>> RemoveConversationMemberForChatAsync(
        Guid conversationId,
        Guid targetUserId,
        CancellationToken cancellationToken = default) =>
        conversationId == Guid.Empty || targetUserId == Guid.Empty
            ? Task.FromResult(ClientAdminRequestResult<bool>.Failure(
                ClientAdminRequestStatus.ValidationFailed))
            : RunAuthenticatedAsync(
                token => transport.SendNoContentAsync<object>(
                    HttpMethod.Delete,
                    $"api/conversations/{conversationId:D}/members/{targetUserId:D}",
                    null,
                    token),
                cancellationToken);

    public Task<ClientAdminRequestStatus> LoadPrivateMembersAsync(
        Guid channelId,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            if (channelId == Guid.Empty)
            {
                return ClientAdminRequestStatus.ValidationFailed;
            }

            var members = await transport
                .GetAsync<IReadOnlyList<ConversationMemberDto>>(
                    $"api/conversations/{channelId:D}/members",
                    token)
                .ConfigureAwait(false);
            if (members.Status == ClientAdminRequestStatus.Completed)
            {
                Publish(Snapshot with
                {
                    SelectedPrivateChannelId = channelId,
                    PrivateMembers = members.Value!,
                });
            }

            return members.Status;
        }, cancellationToken, waitForGate: true);

    public void ClearPrivateMembers()
    {
        if (Volatile.Read(ref disposed) == 0 && Snapshot.IsAdmin)
        {
            Publish(Snapshot with
            {
                SelectedPrivateChannelId = null,
                PrivateMembers = Array.Empty<ConversationMemberDto>(),
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        Action<ClientAdminSnapshot>? handlers;
        lock (stateGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            Volatile.Write(ref snapshot, ClientAdminSnapshot.Hidden);
            handlers = SnapshotChanged;
        }

        lifetimeCancellation.Cancel();
        handlers?.Invoke(ClientAdminSnapshot.Hidden);
        return ValueTask.CompletedTask;
    }

    private Task<ClientAdminRequestStatus> MutateAsync<TRequest>(
        HttpMethod method, string uri, TRequest? request, CancellationToken cancellationToken)
        where TRequest : class =>
        RunAsync(async token =>
        {
            var result = await transport.SendNoContentAsync(method, uri, request, token).ConfigureAwait(false);
            if (result.Status == ClientAdminRequestStatus.Completed)
            {
                return await RefreshInsideGateAsync(token).ConfigureAwait(false);
            }

            return result.Status;
        }, cancellationToken);

    private async Task<ClientAdminRequestStatus> RunAsync(
        Func<CancellationToken, Task<ClientAdminRequestStatus>> operation,
        CancellationToken cancellationToken,
        bool waitForGate = false)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return ClientAdminRequestStatus.Canceled;
        }

        if (!Snapshot.IsAdmin)
        {
            return ClientAdminRequestStatus.AccessDenied;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var enteredGate = false;
        try
        {
            if (waitForGate)
            {
                await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            else if (!await operationGate.WaitAsync(0, linkedCancellation.Token).ConfigureAwait(false))
            {
                return ClientAdminRequestStatus.Canceled;
            }

            enteredGate = true;
            if (Volatile.Read(ref disposed) != 0)
            {
                return ClientAdminRequestStatus.Canceled;
            }

            Publish(Snapshot with { IsBusy = true, LastStatus = null });
            var outcome = await operation(linkedCancellation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0)
            {
                return ClientAdminRequestStatus.Canceled;
            }

            if (outcome is ClientAdminRequestStatus.AuthenticationRequired or ClientAdminRequestStatus.AccessDenied)
            {
                Publish(ClientAdminSnapshot.Hidden);
                if (outcome == ClientAdminRequestStatus.AuthenticationRequired)
                {
                    await NotifyAuthenticationRequiredAsync().ConfigureAwait(false);
                }

                return outcome;
            }

            Publish(Snapshot with { IsBusy = false, LastStatus = outcome });
            return outcome;
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                Publish(Snapshot with { IsBusy = false, LastStatus = ClientAdminRequestStatus.Canceled });
            }

            return ClientAdminRequestStatus.Canceled;
        }
        finally
        {
            if (enteredGate)
            {
                operationGate.Release();
            }
        }
    }

    private async Task<ClientAdminRequestResult<T>> RunAuthenticatedAsync<T>(
        Func<CancellationToken, Task<ClientAdminRequestResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return ClientAdminRequestResult<T>.Failure(ClientAdminRequestStatus.Canceled);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var enteredGate = false;
        try
        {
            await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            enteredGate = true;
            if (Volatile.Read(ref disposed) != 0)
            {
                return ClientAdminRequestResult<T>.Failure(ClientAdminRequestStatus.Canceled);
            }

            var result = await operation(linkedCancellation.Token).ConfigureAwait(false);
            if (result.Status == ClientAdminRequestStatus.AuthenticationRequired)
            {
                await NotifyAuthenticationRequiredAsync().ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return ClientAdminRequestResult<T>.Failure(ClientAdminRequestStatus.Canceled);
        }
        finally
        {
            if (enteredGate)
            {
                operationGate.Release();
            }
        }
    }

    private async Task<ClientAdminRequestStatus> RefreshInsideGateAsync(CancellationToken token)
    {
        var users = await transport.GetAsync<IReadOnlyList<AdminUserResponse>>("api/admin/users", token).ConfigureAwait(false);
        var channels = users.Status == ClientAdminRequestStatus.Completed
            ? await transport.GetAsync<IReadOnlyList<AdminChannelResponse>>("api/admin/channels", token).ConfigureAwait(false)
            : ClientAdminRequestResult<IReadOnlyList<AdminChannelResponse>>.Failure(users.Status);
        var status = channels.Status == ClientAdminRequestStatus.Completed
            ? await transport.GetAsync<ServerStatusResponse>("api/admin/status", token).ConfigureAwait(false)
            : ClientAdminRequestResult<ServerStatusResponse>.Failure(channels.Status);
        var settings = status.Status == ClientAdminRequestStatus.Completed
            ? await transport.GetAsync<UploadSettingsResponse>("api/admin/settings/upload", token).ConfigureAwait(false)
            : ClientAdminRequestResult<UploadSettingsResponse>.Failure(status.Status);
        if (settings.Status == ClientAdminRequestStatus.Completed)
        {
            Publish(new ClientAdminSnapshot(
                true,
                true,
                null,
                users.Value!,
                channels.Value!,
                status.Value,
                settings.Value,
                null,
                Array.Empty<ConversationMemberDto>()));
        }

        return settings.Status;
    }

    private void Publish(ClientAdminSnapshot value)
    {
        lock (stateGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref snapshot, value);
            SnapshotChanged?.Invoke(value);
        }
    }

    private async Task NotifyAuthenticationRequiredAsync()
    {
        var handlers = AuthenticationRequired;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            await handler().ConfigureAwait(false);
        }
    }
}
