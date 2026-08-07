using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Users;

namespace RelayCove.Server.Services;

public sealed class UserDirectoryQueryService(RelayCoveDbContext dbContext)
{
    public async Task<ConversationOperationResult<IReadOnlyList<UserDirectoryEntryDto>>> ListAsync(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            !await dbContext.Users.AsNoTracking().AnyAsync(
                user => user.Id == actorUserId && !user.IsDisabled && user.RetiredAt == null,
                cancellationToken))
        {
            return new ConversationOperationResult<IReadOnlyList<UserDirectoryEntryDto>>(
                ConversationOperationStatus.AccessDenied);
        }

        var users = await dbContext.Users
            .AsNoTracking()
            .Where(user => !user.IsDisabled && user.RetiredAt == null)
            .OrderBy(user => user.NormalizedUserName)
            .ThenBy(user => user.Id)
            .Select(user => new UserDirectoryEntryDto(
                user.Id,
                user.UserName,
                user.DisplayName))
            .ToArrayAsync(cancellationToken);
        return new ConversationOperationResult<IReadOnlyList<UserDirectoryEntryDto>>(
            ConversationOperationStatus.Success,
            Array.AsReadOnly(users));
    }
}
