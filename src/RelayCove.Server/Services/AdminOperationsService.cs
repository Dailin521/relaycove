using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Services;

public sealed class AdminOperationsService(
    RelayCoveDbContext dbContext,
    UploadSettingsService uploadSettingsService,
    ServerRuntimeMetrics runtimeMetrics,
    ServerClock clock,
    IOptions<StorageOptions> storageOptions)
{
    public async Task<IReadOnlyList<AdminChannelResponse>> ListChannelsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Conversations
            .AsNoTracking()
            .Where(conversation =>
                !conversation.IsDeleted &&
                (conversation.Type == ConversationType.PublicChannel ||
                 conversation.Type == ConversationType.PrivateChannel))
            .OrderBy(conversation => conversation.Name)
            .ThenBy(conversation => conversation.Id)
            .Select(conversation => new AdminChannelResponse(
                conversation.Id,
                conversation.Type,
                conversation.Name,
                new DateTimeOffset(conversation.CreatedAt),
                new DateTimeOffset(conversation.UpdatedAt)))
            .ToArrayAsync(cancellationToken);

    public async Task<ServerStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var effectiveUploadLimit = await uploadSettingsService.GetEffectiveMaximumFileBytesAsync(cancellationToken);
        var startedAt = runtimeMetrics.StartedAt;
        var uptime = clock.UtcNow - startedAt.UtcDateTime;
        var lastError = runtimeMetrics.GetLastError();
        return new ServerStatusResponse(
            GetVersion(),
            startedAt,
            Math.Max(0, (long)uptime.TotalSeconds),
            runtimeMetrics.OnlineConnectionCount,
            GetFileBytes(dbContext.Database.GetDbConnection().DataSource),
            GetDirectoryBytes(storageOptions.Value.UploadsPath),
            effectiveUploadLimit,
            lastError?.Category,
            lastError?.OccurredAt);
    }

    private static string GetVersion() =>
        typeof(AdminOperationsService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(AdminOperationsService).Assembly.GetName().Version?.ToString() ??
        "unknown";

    private static long GetFileBytes(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new FileInfo(path).Length
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long GetDirectoryBytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total = checked(total + new FileInfo(file).Length);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }
}
