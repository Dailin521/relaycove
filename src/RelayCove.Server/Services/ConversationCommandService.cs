using System.Data;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Services;

public sealed class ConversationCommandService(
    RelayCoveDbContext dbContext,
    ServerClock clock,
    ILogger<ConversationCommandService> logger)
{
    // Newly created conversations do not have messages yet.
    private const long EmptyConversationJoinWatermark = 0;

    public async Task<ConversationOperationResult<ConversationDto>> CreateAsync(
        Guid actorUserId,
        CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Type))
        {
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        return request.Type == ConversationType.Direct
            ? await CreateDirectAsync(actorUserId, request.ParticipantUserId, cancellationToken)
            : await CreateChannelAsync(actorUserId, request.Type, request.Name, cancellationToken);
    }

    public async Task<ConversationOperationResult<ConversationMemberDto>> UpsertMemberAsync(
        Guid actorUserId,
        Guid conversationId,
        UpsertConversationMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty || request.UserId == Guid.Empty || !Enum.IsDefined(request.Role))
        {
            return new ConversationOperationResult<ConversationMemberDto>(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actor = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == actorUserId && !user.IsDisabled,
            cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationMemberDto>(ConversationOperationStatus.AccessDenied);
        }

        var conversation = await dbContext.Conversations.SingleOrDefaultAsync(
            candidate => candidate.Id == conversationId && !candidate.IsDeleted,
            cancellationToken);
        if (conversation is not null && conversation.Type != ConversationType.PublicChannel)
        {
            await dbContext.Entry(conversation)
                .Collection(candidate => candidate.Members)
                .LoadAsync(cancellationToken);
        }

        var authorization = GetMemberWriteAuthorization(conversation, actor);
        if (authorization != ConversationOperationStatus.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            LogDeniedMemberWrite(actorUserId, conversationId, authorization);
            return new ConversationOperationResult<ConversationMemberDto>(authorization);
        }

        var target = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == request.UserId && !user.IsDisabled,
            cancellationToken);
        if (target is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Conversation member upsert by {ActorUserId} could not resolve target {TargetUserId} for {ConversationId}.",
                actorUserId,
                request.UserId,
                conversationId);
            return new ConversationOperationResult<ConversationMemberDto>(ConversationOperationStatus.UserNotFound);
        }

        var existingMember = conversation!.Members.SingleOrDefault(member => member.UserId == request.UserId);
        var status = ConversationOperationStatus.Success;
        if (existingMember is null)
        {
            var now = clock.UtcNow;
            var joinWatermark = await dbContext.Messages
                .Where(message => message.ConversationId == conversation.Id)
                .Select(message => (long?)message.Id)
                .MaxAsync(cancellationToken) ?? EmptyConversationJoinWatermark;
            existingMember = new ConversationMember(
                conversation.Id,
                target.Id,
                request.Role,
                now,
                lastReadMessageId: joinWatermark);
            dbContext.ConversationMembers.Add(existingMember);
            conversation.Touch(now);
            status = ConversationOperationStatus.Created;
        }
        else if (existingMember.Role != request.Role)
        {
            existingMember.SetRole(request.Role);
            conversation.Touch(clock.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Conversation member upsert by {ActorUserId} for target {TargetUserId} in {ConversationId}; role={Role}; result={Result}.",
            actorUserId,
            target.Id,
            conversation.Id,
            request.Role,
            status);
        return new ConversationOperationResult<ConversationMemberDto>(
            status,
            ToMemberDto(existingMember, target));
    }

    public async Task<ConversationOperationStatus> RemoveMemberAsync(
        Guid actorUserId,
        Guid conversationId,
        Guid targetUserId,
        CancellationToken cancellationToken) =>
        (await RemoveMemberWithResultAsync(
            actorUserId,
            conversationId,
            targetUserId,
            cancellationToken)).Status;

    public async Task<ConversationOperationResult<ConversationDto>> UpdateChannelAsync(
        Guid actorUserId,
        Guid conversationId,
        UpdateConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty || request.Name is null)
        {
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actorIsAdministrator = await dbContext.Users.AnyAsync(
            user => user.Id == actorUserId && !user.IsDisabled && user.RetiredAt == null && user.IsAdmin,
            cancellationToken);
        if (!actorIsAdministrator)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessDenied);
        }

        var conversation = await dbContext.Conversations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == conversationId && !candidate.IsDeleted,
                cancellationToken);
        if (conversation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessRevoked);
        }

        if (conversation.Type == ConversationType.Direct)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.ConversationTypeConflict);
        }

        try
        {
            conversation.Rename(request.Name, clock.UtcNow);
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrator {ActorUserId} renamed conversation {ConversationId}.",
            actorUserId,
            conversationId);
        return new ConversationOperationResult<ConversationDto>(
            ConversationOperationStatus.Success,
            ToConversationDto(conversation, conversation.Name, 0, false));
    }

    public async Task<ConversationChannelDeleteResult> DeleteChannelAsync(
        Guid actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty)
        {
            return new ConversationChannelDeleteResult(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actorIsAdministrator = await dbContext.Users.AnyAsync(
            user => user.Id == actorUserId && !user.IsDisabled && user.RetiredAt == null && user.IsAdmin,
            cancellationToken);
        if (!actorIsAdministrator)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationChannelDeleteResult(ConversationOperationStatus.AccessDenied);
        }

        var conversation = await dbContext.Conversations
            .SingleOrDefaultAsync(
                candidate => candidate.Id == conversationId && !candidate.IsDeleted,
                cancellationToken);
        if (conversation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationChannelDeleteResult(ConversationOperationStatus.AccessRevoked);
        }

        if (conversation.Type == ConversationType.Direct)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationChannelDeleteResult(ConversationOperationStatus.ConversationTypeConflict);
        }

        IReadOnlyList<Guid> revokedUserIds;
        if (conversation.Type == ConversationType.PublicChannel)
        {
            revokedUserIds = await dbContext.Users
                .Where(user => !user.IsDisabled && user.RetiredAt == null)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            revokedUserIds = await dbContext.ConversationMembers
                .Where(member => member.ConversationId == conversationId)
                .Select(member => member.UserId)
                .ToArrayAsync(cancellationToken);
        }

        conversation.MarkDeleted(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrator {ActorUserId} deleted conversation {ConversationId}; recipients={RecipientCount}.",
            actorUserId,
            conversationId,
            revokedUserIds.Count);
        return new ConversationChannelDeleteResult(ConversationOperationStatus.NoContent, revokedUserIds);
    }

    internal async Task<ConversationMemberRemovalResult> RemoveMemberWithResultAsync(
        Guid actorUserId,
        Guid conversationId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty || targetUserId == Guid.Empty)
        {
            return new ConversationMemberRemovalResult(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actor = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == actorUserId && !user.IsDisabled,
            cancellationToken);
        if (actor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationMemberRemovalResult(ConversationOperationStatus.AccessDenied);
        }

        var conversation = await dbContext.Conversations.SingleOrDefaultAsync(
            candidate => candidate.Id == conversationId && !candidate.IsDeleted,
            cancellationToken);
        if (conversation is not null && conversation.Type != ConversationType.PublicChannel)
        {
            await dbContext.Entry(conversation)
                .Collection(candidate => candidate.Members)
                .LoadAsync(cancellationToken);
        }

        var authorization = GetMemberWriteAuthorization(conversation, actor);
        if (authorization != ConversationOperationStatus.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            LogDeniedMemberWrite(actorUserId, conversationId, authorization);
            return new ConversationMemberRemovalResult(authorization);
        }

        var member = conversation!.Members.SingleOrDefault(candidate => candidate.UserId == targetUserId);
        if (member is not null)
        {
            dbContext.ConversationMembers.Remove(member);
            conversation.Touch(clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Conversation member removal by {ActorUserId} for target {TargetUserId} in {ConversationId}; removed={Removed}.",
            actorUserId,
            targetUserId,
            conversation.Id,
            member is not null);
        return new ConversationMemberRemovalResult(
            ConversationOperationStatus.NoContent,
            member?.UserId);
    }

    private async Task<ConversationOperationResult<ConversationDto>> CreateChannelAsync(
        Guid actorUserId,
        ConversationType type,
        string? name,
        CancellationToken cancellationToken)
    {
        if (type is not ConversationType.PublicChannel and not ConversationType.PrivateChannel || name is null)
        {
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actorIsActive = await dbContext.Users.AnyAsync(
            user => user.Id == actorUserId && !user.IsDisabled && user.RetiredAt == null,
            cancellationToken);
        if (!actorIsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(
                "User {ActorUserId} failed the in-transaction channel creation active-user recheck.",
                actorUserId);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessDenied);
        }

        var now = clock.UtcNow;
        Conversation conversation;
        try
        {
            conversation = Conversation.CreateChannel(
                Guid.NewGuid(),
                type,
                name,
                actorUserId,
                now);
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        var creator = new ConversationMember(
            conversation.Id,
            actorUserId,
            ConversationMemberRole.Administrator,
            conversation.CreatedAt,
            lastReadMessageId: EmptyConversationJoinWatermark);
        dbContext.Conversations.Add(conversation);
        dbContext.ConversationMembers.Add(creator);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "User {ActorUserId} created conversation {ConversationId}; type={ConversationType}.",
            actorUserId,
            conversation.Id,
            type);
        return new ConversationOperationResult<ConversationDto>(
            ConversationOperationStatus.Created,
            ToConversationDto(
                conversation,
                conversation.Name,
                creator.LastReadMessageId,
                creator.IsMuted));
    }

    private async Task<ConversationOperationResult<ConversationDto>> CreateDirectAsync(
        Guid actorUserId,
        Guid? participantUserId,
        CancellationToken cancellationToken)
    {
        if (!participantUserId.HasValue ||
            participantUserId.Value == Guid.Empty ||
            participantUserId.Value == actorUserId)
        {
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actorIsActive = await dbContext.Users.AnyAsync(
            user => user.Id == actorUserId && !user.IsDisabled,
            cancellationToken);
        if (!actorIsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.AccessDenied);
        }

        var participant = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == participantUserId.Value && !user.IsDisabled,
            cancellationToken);
        if (participant is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Direct conversation request by {ActorUserId} could not resolve target {TargetUserId}.",
                actorUserId,
                participantUserId.Value);
            return new ConversationOperationResult<ConversationDto>(ConversationOperationStatus.UserNotFound);
        }

        var directKey = Conversation.CreateDirectParticipantKey(actorUserId, participant.Id);
        var existing = await dbContext.Conversations
            .Include(conversation => conversation.Members)
            .SingleOrDefaultAsync(
                conversation => conversation.DirectParticipantKey == directKey,
                cancellationToken);
        if (existing is not null)
        {
            EnsureDirectInvariant(existing, actorUserId, participant.Id);
            var restored = existing.IsDeleted;
            if (restored)
            {
                existing.Restore(clock.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "User {ActorUserId} obtained existing direct conversation {ConversationId}; restored={Restored}.",
                actorUserId,
                existing.Id,
                restored);
            var actorMember = existing.Members.Single(member => member.UserId == actorUserId);
            return new ConversationOperationResult<ConversationDto>(
                ConversationOperationStatus.Success,
                ToConversationDto(
                    existing,
                    participant.DisplayName,
                    actorMember.LastReadMessageId,
                    actorMember.IsMuted));
        }

        var now = clock.UtcNow;
        var conversation = Conversation.CreateDirect(
            Guid.NewGuid(),
            actorUserId,
            participant.Id,
            actorUserId,
            now);
        var actorMembership = new ConversationMember(
            conversation.Id,
            actorUserId,
            ConversationMemberRole.Member,
            conversation.CreatedAt,
            lastReadMessageId: EmptyConversationJoinWatermark);
        var participantMembership = new ConversationMember(
            conversation.Id,
            participant.Id,
            ConversationMemberRole.Member,
            conversation.CreatedAt,
            lastReadMessageId: EmptyConversationJoinWatermark);
        dbContext.Conversations.Add(conversation);
        dbContext.ConversationMembers.AddRange(actorMembership, participantMembership);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "User {ActorUserId} created direct conversation {ConversationId} with target {TargetUserId}.",
            actorUserId,
            conversation.Id,
            participant.Id);
        return new ConversationOperationResult<ConversationDto>(
            ConversationOperationStatus.Created,
            ToConversationDto(
                conversation,
                participant.DisplayName,
                actorMembership.LastReadMessageId,
                actorMembership.IsMuted));
    }

    private static ConversationOperationStatus GetMemberWriteAuthorization(
        Conversation? conversation,
        User actor)
    {
        if (conversation is null)
        {
            return ConversationOperationStatus.AccessRevoked;
        }

        var actorMember = conversation.Members.SingleOrDefault(member => member.UserId == actor.Id);
        return conversation.Type switch
        {
            ConversationType.PublicChannel => ConversationOperationStatus.ConversationTypeConflict,
            ConversationType.Direct when actorMember is not null => ConversationOperationStatus.ConversationTypeConflict,
            ConversationType.Direct => ConversationOperationStatus.AccessRevoked,
            ConversationType.PrivateChannel when actor.IsAdmin => ConversationOperationStatus.Success,
            ConversationType.PrivateChannel when actorMember?.Role == ConversationMemberRole.Administrator =>
                ConversationOperationStatus.Success,
            ConversationType.PrivateChannel when actorMember is not null => ConversationOperationStatus.AccessDenied,
            ConversationType.PrivateChannel => ConversationOperationStatus.AccessRevoked,
            _ => throw new InvalidOperationException("A stored conversation has an unknown type."),
        };
    }

    private static void EnsureDirectInvariant(
        Conversation conversation,
        Guid actorUserId,
        Guid participantUserId)
    {
        var expectedUserIds = new HashSet<Guid> { actorUserId, participantUserId };
        if (conversation.Type != ConversationType.Direct ||
            conversation.Members.Count != 2 ||
            conversation.Members.Any(member =>
                !expectedUserIds.Contains(member.UserId) ||
                member.Role != ConversationMemberRole.Member))
        {
            throw new InvalidOperationException(
                $"Direct conversation {conversation.Id:D} violates its participant invariant.");
        }
    }

    private static ConversationDto ToConversationDto(
        Conversation conversation,
        string displayName,
        long lastReadMessageId,
        bool isMuted) =>
        new(
            conversation.Id,
            conversation.Type,
            displayName,
            conversation.AvatarAttachmentId is Guid avatarAttachmentId
                ? $"/api/attachments/{avatarAttachmentId:D}"
                : null,
            new DateTimeOffset(conversation.CreatedAt),
            new DateTimeOffset(conversation.UpdatedAt),
            LastMessageId: 0,
            lastReadMessageId,
            UnreadCount: 0,
            isMuted);

    private static ConversationMemberDto ToMemberDto(
        ConversationMember member,
        User user) =>
        new(
            user.Id,
            user.UserName,
            user.DisplayName,
            member.Role,
            new DateTimeOffset(member.JoinedAt),
            member.LastReadMessageId,
            member.IsMuted);

    private void LogDeniedMemberWrite(
        Guid actorUserId,
        Guid conversationId,
        ConversationOperationStatus status)
    {
        logger.LogWarning(
            "Conversation member write by {ActorUserId} for {ConversationId} was denied; result={Result}.",
            actorUserId,
            conversationId,
            status);
    }
}
