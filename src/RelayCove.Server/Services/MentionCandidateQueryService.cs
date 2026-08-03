using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MentionCandidateQueryService(
    RelayCoveDbContext dbContext,
    ILogger<MentionCandidateQueryService> logger)
{
    public async Task<ConversationOperationResult<MentionCandidateListResponse>> ListAsync(
        Guid actorUserId,
        Guid conversationId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            conversationId == Guid.Empty ||
            !MentionCandidateQueryValidator.IsValidQuery(query) ||
            limit is < 1 or > MentionCandidateQueryValidator.MaximumLimit)
        {
            return new ConversationOperationResult<MentionCandidateListResponse>(
                ConversationOperationStatus.InvalidRequest);
        }

        var visibleConversation = ConversationAccessQuery
            .VisibleTo(dbContext, actorUserId)
            .Where(conversation => conversation.Id == conversationId);
        var normalizedQuery = query.ToUpperInvariant();
        var escapedPrefix = EscapeLikePattern(normalizedQuery) + "%";
        var rows = await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                !user.IsDisabled &&
                EF.Functions.Like(user.NormalizedUserName, escapedPrefix, "\\") &&
                visibleConversation.Any(conversation =>
                    conversation.Type == ConversationType.PublicChannel ||
                    conversation.Members.Any(member => member.UserId == user.Id)))
            .OrderBy(user => user.NormalizedUserName)
            .ThenBy(user => user.Id)
            .Select(user => new MentionCandidateDto(
                user.Id,
                user.UserName,
                user.DisplayName))
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0 &&
            !await visibleConversation.AnyAsync(cancellationToken))
        {
            logger.LogInformation(
                "Mention candidate read by {ActorUserId} for {ConversationId} was denied.",
                actorUserId,
                conversationId);
            return new ConversationOperationResult<MentionCandidateListResponse>(
                ConversationOperationStatus.AccessRevoked);
        }

        var hasMore = rows.Length > limit;
        var candidates = hasMore ? rows[..limit] : rows;
        return new ConversationOperationResult<MentionCandidateListResponse>(
            ConversationOperationStatus.Success,
            new MentionCandidateListResponse(
                conversationId,
                Array.AsReadOnly(candidates),
                hasMore));
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
