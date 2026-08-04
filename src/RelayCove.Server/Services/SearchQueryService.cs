using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class SearchQueryService(
    RelayCoveDbContext dbContext,
    ILogger<SearchQueryService> logger)
{
    public async Task<SearchOperationResult<SearchResponse>> SearchAsync(
        Guid actorUserId,
        string normalizedKeyword,
        Guid? conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            !SearchQueryValidator.IsValidNormalizedKeyword(normalizedKeyword) ||
            limit is < 1 or > SearchQueryValidator.MaximumLimit)
        {
            return new SearchOperationResult<SearchResponse>(
                SearchOperationStatus.InvalidRequest);
        }

        var searchScope = conversationId.HasValue ? "scoped" : "global";
        IQueryable<Conversation> visibleConversations =
            ConversationAccessQuery.VisibleTo(dbContext, actorUserId);
        if (conversationId.HasValue)
        {
            var requiredConversationId = conversationId.Value;
            visibleConversations = visibleConversations.Where(
                conversation => conversation.Id == requiredConversationId);
        }

        var likePattern = $"%{EscapeLike(normalizedKeyword)}%";
        var rows = await dbContext.Messages
            .AsNoTracking()
            .Where(message => visibleConversations.Any(
                conversation => conversation.Id == message.ConversationId))
            .Where(message =>
                message.Content != null &&
                EF.Functions.Like(message.Content, likePattern, "\\") ||
                message.Attachments.Any(attachment => EF.Functions.Like(
                    attachment.OriginalFileName,
                    likePattern,
                    "\\")))
            .OrderByDescending(message => message.Id)
            .Select(message => new SearchRow(
                message.Id,
                message.ConversationId,
                message.Conversation.Type == ConversationType.Direct
                    ? message.Conversation.Members
                        .Where(member => member.UserId != actorUserId)
                        .Select(member => member.User.DisplayName)
                        .FirstOrDefault()
                    : message.Conversation.Name,
                message.Sender.DisplayName,
                message.Content,
                message.CreatedAt,
                message.Content != null &&
                    EF.Functions.Like(message.Content, likePattern, "\\"),
                message.Attachments
                    .Where(attachment => EF.Functions.Like(
                        attachment.OriginalFileName,
                        likePattern,
                        "\\"))
                    .OrderBy(attachment => attachment.Id)
                    .Select(attachment => attachment.OriginalFileName)
                    .FirstOrDefault()))
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0 && conversationId.HasValue &&
            !await visibleConversations.AnyAsync(cancellationToken))
        {
            logger.LogInformation(
                "Search denied; scope={SearchScope}.",
                searchScope);
            return new SearchOperationResult<SearchResponse>(
                SearchOperationStatus.AccessRevoked);
        }

        var hasMore = rows.Length > limit;
        var results = rows
            .Take(limit)
            .Select(row => new SearchResultDto(
                row.MessageId,
                row.ConversationId,
                row.ConversationName ?? string.Empty,
                row.SenderName,
                SearchSnippet.Create(row.Content, normalizedKeyword, row.ContentMatched),
                new DateTimeOffset(row.CreatedAt),
                row.MatchedAttachmentFileName))
            .ToArray();
        logger.LogInformation(
            "Search completed; scope={SearchScope}, resultCount={ResultCount}, hasMore={HasMore}.",
            searchScope,
            results.Length,
            hasMore);

        return new SearchOperationResult<SearchResponse>(
            SearchOperationStatus.Success,
            new SearchResponse(results, hasMore));
    }

    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record SearchRow(
        long MessageId,
        Guid ConversationId,
        string? ConversationName,
        string SenderName,
        string? Content,
        DateTime CreatedAt,
        bool ContentMatched,
        string? MatchedAttachmentFileName);
}
