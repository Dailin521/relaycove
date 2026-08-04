using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Services;

namespace RelayCove.Server.Hubs;

[Authorize]
public sealed class ChatHub(
    RelayCoveDbContext dbContext,
    ServerRuntimeMetrics runtimeMetrics,
    ILogger<ChatHub> logger) : Hub<IChatClient>
{
    public const string Route = "/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        var subject = Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParseExact(subject, "D", out var actorUserId) || actorUserId == Guid.Empty)
        {
            Context.Abort();
            return;
        }

        var rawTokenVersion = Context.User?.FindFirst(AccessTokenService.AccessTokenVersionClaimType)?.Value;
        var accessTokenVersion = 0L;
        if (rawTokenVersion is not null &&
            (!long.TryParse(rawTokenVersion, NumberStyles.None, CultureInfo.InvariantCulture, out accessTokenVersion) ||
             accessTokenVersion < 0))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            AccountHubGroup.For(actorUserId, accessTokenVersion),
            Context.ConnectionAborted);

        var conversationIds = await ConversationAccessQuery
            .VisibleTo(dbContext, actorUserId)
            .Select(conversation => conversation.Id)
            .ToArrayAsync(Context.ConnectionAborted);
        foreach (var conversationId in conversationIds)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                ConversationHubGroup.For(conversationId),
                Context.ConnectionAborted);
        }

        logger.LogInformation(
            "SignalR user {UserId} connected and joined {ConversationCount} conversation groups.",
            actorUserId,
            conversationIds.Length);
        runtimeMetrics.RecordConnection(Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        runtimeMetrics.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
