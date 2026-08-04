using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;

namespace RelayCove.Server.Services;

public sealed class UploadSettingsService(
    RelayCoveDbContext dbContext,
    IOptions<UploadOptions> uploadOptions,
    ServerClock clock)
{
    public const string MaximumFileBytesKey = "Uploads.MaximumFileBytes";
    public const long MinimumMaximumFileBytes = UploadOptions.MinimumMaximumFileBytes;

    public async Task<long> GetEffectiveMaximumFileBytesAsync(CancellationToken cancellationToken)
    {
        var value = await dbContext.AppSettings
            .AsNoTracking()
            .Where(setting => setting.Key == MaximumFileBytesKey)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (value is null)
        {
            return uploadOptions.Value.MaximumFileBytes;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumFileBytes) ||
            !IsValidMaximumFileBytes(maximumFileBytes))
        {
            throw new InvalidOperationException("The persisted upload maximum is invalid.");
        }

        return maximumFileBytes;
    }

    public async Task<UploadSettingsUpdateResult> SetEffectiveMaximumFileBytesAsync(
        Guid actorUserId,
        long maximumFileBytes,
        CancellationToken cancellationToken)
    {
        if (!IsValidMaximumFileBytes(maximumFileBytes))
        {
            return new UploadSettingsUpdateResult(UploadSettingsUpdateStatus.InvalidRequest);
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
            return new UploadSettingsUpdateResult(UploadSettingsUpdateStatus.AccessDenied);
        }

        var value = maximumFileBytes.ToString(CultureInfo.InvariantCulture);
        var existing = await dbContext.AppSettings.SingleOrDefaultAsync(
            setting => setting.Key == MaximumFileBytesKey,
            cancellationToken);
        if (existing is null)
        {
            dbContext.AppSettings.Add(new AppSetting(MaximumFileBytesKey, value, clock.UtcNow));
        }
        else
        {
            existing.SetValue(value, clock.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UploadSettingsUpdateResult(UploadSettingsUpdateStatus.Success, maximumFileBytes);
    }

    public static bool IsValidMaximumFileBytes(long value) =>
        value >= MinimumMaximumFileBytes && value <= UploadOptions.AbsoluteMaximumFileBytes;
}
