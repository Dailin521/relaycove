using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Services;

public sealed class ConversationQueryService(
    RelayCoveDbContext dbContext,
    ILogger<ConversationQueryService> logger)
{
    public async Task<ConversationOperationResult<ConversationListResponse>> ListAsync(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var conversations = BuildVisibleConversations(actorUserId)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenBy(conversation => conversation.Id);
        var rows = await ProjectConversations(conversations, actorUserId)
            .ToArrayAsync(cancellationToken);
        var response = new ConversationListResponse(
            rows.Select(ToConversationDto).ToArray(),
            Complete: true);
        return new ConversationOperationResult<ConversationListResponse>(
            ConversationOperationStatus.Success,
            response);
    }

    public async Task<ConversationOperationResult<ConversationDto>> GetAsync(
        Guid actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty)
        {
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessRevoked);
        }

        var row = await ProjectConversations(
                BuildVisibleConversations(actorUserId, conversationId),
                actorUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            logger.LogInformation(
                "Conversation read by {ActorUserId} for {ConversationId} was denied.",
                actorUserId,
                conversationId);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessRevoked);
        }

        return new ConversationOperationResult<ConversationDto>(
            ConversationOperationStatus.Success,
            ToConversationDto(row));
    }

    public async Task<ConversationOperationResult<ConversationMemberListResponse>> ListMembersAsync(
        Guid actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty)
        {
            return new ConversationOperationResult<ConversationMemberListResponse>(
                ConversationOperationStatus.AccessRevoked);
        }

        var visibleType = await (
                from actor in dbContext.Users.AsNoTracking()
                where actor.Id == actorUserId && !actor.IsDisabled
                from conversation in dbContext.Conversations.AsNoTracking()
                where conversation.Id == conversationId &&
                    !conversation.IsDeleted &&
                    (conversation.Type == ConversationType.PublicChannel ||
                     conversation.Type == ConversationType.PrivateChannel &&
                     (actor.IsAdmin || conversation.Members.Any(member => member.UserId == actorUserId)) ||
                     conversation.Type == ConversationType.Direct &&
                     conversation.Members.Any(member => member.UserId == actorUserId))
                select (ConversationType?)conversation.Type)
            .SingleOrDefaultAsync(cancellationToken);
        if (visibleType is null)
        {
            logger.LogInformation(
                "Conversation member list read by {ActorUserId} for {ConversationId} was denied.",
                actorUserId,
                conversationId);
            return new ConversationOperationResult<ConversationMemberListResponse>(
                ConversationOperationStatus.AccessRevoked);
        }

        if (visibleType == ConversationType.PublicChannel)
        {
            return new ConversationOperationResult<ConversationMemberListResponse>(
                ConversationOperationStatus.ConversationTypeConflict);
        }

        var rows = await (
                from actor in dbContext.Users.AsNoTracking()
                where actor.Id == actorUserId && !actor.IsDisabled
                from conversation in dbContext.Conversations.AsNoTracking()
                where conversation.Id == conversationId &&
                    !conversation.IsDeleted &&
                    (conversation.Type == ConversationType.PublicChannel ||
                     conversation.Type == ConversationType.PrivateChannel &&
                     (actor.IsAdmin || conversation.Members.Any(member => member.UserId == actorUserId)) ||
                     conversation.Type == ConversationType.Direct &&
                     conversation.Members.Any(member => member.UserId == actorUserId))
                from member in conversation.Members.DefaultIfEmpty()
                orderby member.JoinedAt, member.UserId
                select new ConversationMemberProjection(
                    conversation.Id,
                    conversation.Type,
                    member != null,
                    member == null ? null : member.UserId,
                    member == null ? null : member.User.UserName,
                    member == null ? null : member.User.DisplayName,
                    member == null ? null : (ConversationMemberRole?)member.Role,
                    member == null ? null : member.JoinedAt,
                    member == null ? null : member.LastReadMessageId,
                    member == null ? null : member.IsMuted))
            .AsSingleQuery()
            .ToArrayAsync(cancellationToken);
        if (rows.Length == 0)
        {
            logger.LogInformation(
                "Conversation member list read by {ActorUserId} for {ConversationId} was denied.",
                actorUserId,
                conversationId);
            return new ConversationOperationResult<ConversationMemberListResponse>(
                ConversationOperationStatus.AccessRevoked);
        }

        var members = rows
            .Where(row => row.HasMember)
            .Select(row => new ConversationMemberDto(
                row.UserId!.Value,
                row.UserName!,
                row.DisplayName!,
                row.Role!.Value,
                new DateTimeOffset(row.JoinedAt!.Value),
                row.LastReadMessageId!.Value,
                row.IsMuted!.Value))
            .ToArray();
        return new ConversationOperationResult<ConversationMemberListResponse>(
            ConversationOperationStatus.Success,
            new ConversationMemberListResponse(conversationId, members));
    }

    private IQueryable<Conversation> BuildVisibleConversations(
        Guid actorUserId,
        Guid? conversationId = null)
    {
        IQueryable<Conversation> candidates = ConversationAccessQuery.VisibleTo(dbContext, actorUserId);
        if (conversationId.HasValue)
        {
            var requiredId = conversationId.Value;
            candidates = candidates.Where(conversation => conversation.Id == requiredId);
        }

        return candidates;
    }

    private static IQueryable<ConversationProjection> ProjectConversations(
        IQueryable<Conversation> conversations,
        Guid actorUserId) =>
        conversations.Select(conversation =>
            new ConversationProjection(
                conversation.Id,
                conversation.Type,
                conversation.Type == ConversationType.Direct
                    ? conversation.Members
                        .Where(member => member.UserId != actorUserId)
                        .Select(member => member.User.DisplayName)
                        .FirstOrDefault()
                    : conversation.Name,
                conversation.AvatarAttachmentId,
                conversation.CreatedAt,
                conversation.UpdatedAt,
                conversation.Members
                    .Where(member => member.UserId == actorUserId)
                    .Select(member => (long?)member.LastReadMessageId)
                    .FirstOrDefault() ?? 0L,
                conversation.Messages
                    .Select(message => (long?)message.Id)
                    .Max() ?? 0L,
                conversation.Messages.Count(message =>
                    message.SenderId != actorUserId &&
                    message.Id > (conversation.Members
                        .Where(member => member.UserId == actorUserId)
                        .Select(member => (long?)member.LastReadMessageId)
                        .FirstOrDefault() ?? 0L))));

    private static ConversationDto ToConversationDto(ConversationProjection conversation) =>
        new(
            conversation.Id,
            conversation.Type,
            conversation.Name ?? string.Empty,
            conversation.AvatarAttachmentId is Guid avatarAttachmentId
                ? $"/api/attachments/{avatarAttachmentId:D}"
                : null,
            new DateTimeOffset(conversation.CreatedAt),
            new DateTimeOffset(conversation.UpdatedAt),
            conversation.LastMessageId,
            conversation.LastReadMessageId,
            conversation.UnreadCount);

    private sealed record ConversationProjection(
        Guid Id,
        ConversationType Type,
        string? Name,
        Guid? AvatarAttachmentId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        long LastReadMessageId,
        long LastMessageId,
        int UnreadCount);

    private sealed record ConversationMemberProjection(
        Guid ConversationId,
        ConversationType Type,
        bool HasMember,
        Guid? UserId,
        string? UserName,
        string? DisplayName,
        ConversationMemberRole? Role,
        DateTime? JoinedAt,
        long? LastReadMessageId,
        bool? IsMuted);
}
