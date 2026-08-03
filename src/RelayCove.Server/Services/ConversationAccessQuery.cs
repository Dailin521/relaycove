using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Services;

public static class ConversationAccessQuery
{
    public static IQueryable<Conversation> VisibleTo(
        RelayCoveDbContext dbContext,
        Guid actorUserId) =>
        dbContext.Conversations
            .AsNoTracking()
            .Where(conversation =>
                dbContext.Users.Any(actor => actor.Id == actorUserId && !actor.IsDisabled) &&
                !conversation.IsDeleted &&
                (conversation.Type == ConversationType.PublicChannel ||
                 conversation.Members.Any(member => member.UserId == actorUserId)));
}
