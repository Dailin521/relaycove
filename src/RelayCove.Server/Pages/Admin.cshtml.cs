using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Authentication;
using RelayCove.Server.Realtime;
using RelayCove.Server.Services;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Pages;

public sealed class AdminModel(
    AdminUserService adminUserService,
    NewUserValidator newUserValidator,
    AdminOperationsService adminOperationsService,
    UploadSettingsService uploadSettingsService,
    ConversationCommandService conversationCommandService,
    ConversationQueryService conversationQueryService,
    IServiceProvider serviceProvider) : PageModel
{
    public ServerStatusResponse Status { get; private set; } = new("-", DateTimeOffset.MinValue, 0, 0, 0, 0, 0, null, null);
    public long UploadLimitBytes { get; private set; }
    public IReadOnlyList<AdminUserResponse> Users { get; private set; } = [];
    public IReadOnlyList<AdminUserResponse> ActiveUsers { get; private set; } = [];
    public IReadOnlyList<AdminChannelResponse> Channels { get; private set; } = [];
    public Dictionary<Guid, IReadOnlyList<ConversationMemberDto>> Members { get; } = [];

    [TempData]
    public string? FeedbackMessage { get; set; }

    [TempData]
    public bool FeedbackIsError { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(WebAdminAuthenticationDefaults.Scheme);
        return RedirectToPage("/Admin/Login");
    }

    public async Task<IActionResult> OnPostUpdateUploadAsync(long maximumMiB, CancellationToken cancellationToken)
    {
        if (maximumMiB is < 1 or > 100)
        {
            return RedirectWithFeedback("附件上限未更新：请输入 1 到 100 MiB。", isError: true);
        }

        var result = await uploadSettingsService.SetEffectiveMaximumFileBytesAsync(
            ActorUserId,
            maximumMiB * 1024 * 1024,
            cancellationToken);
        return result.Status == UploadSettingsUpdateStatus.Success
            ? RedirectWithFeedback("附件上限已更新。")
            : RedirectWithFeedback("附件上限未更新：当前账号已失去管理权限。", isError: true);
    }

    public async Task<IActionResult> OnPostCreateUserAsync(string? userName, string? displayName, string? password, bool isAdmin, CancellationToken cancellationToken)
    {
        if (newUserValidator.Validate(userName, displayName, password).Count > 0)
        {
            return RedirectWithFeedback("用户未创建：请检查账号、显示名和密码要求。", isError: true);
        }

        var result = await adminUserService.CreateUserAsync(
            ActorUserId,
            new CreateUserRequest(userName!, displayName!, password!, isAdmin),
            cancellationToken);
        return result.Status switch
        {
            AdminUserCreationStatus.Created => RedirectWithFeedback("用户已创建。"),
            AdminUserCreationStatus.UserNameAlreadyExists => RedirectWithFeedback("用户未创建：账号已存在。", isError: true),
            _ => RedirectWithFeedback("用户未创建：当前账号已失去管理权限。", isError: true),
        };
    }

    public async Task<IActionResult> OnPostSetDisabledAsync(Guid userId, bool isDisabled, CancellationToken cancellationToken)
    {
        var result = await adminUserService.UpdateDisabledAsync(ActorUserId, userId, isDisabled, cancellationToken);
        await PublishAccountRevocationAsync(result);
        return UserMutationFeedback(result, isDisabled ? "用户已禁用。" : "用户已恢复。", "用户状态未更新。");
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid userId, string? password, CancellationToken cancellationToken)
    {
        var result = await adminUserService.ResetPasswordAsync(ActorUserId, userId, password, cancellationToken);
        await PublishAccountRevocationAsync(result);
        return UserMutationFeedback(result, "密码已重置，旧会话已失效。", "密码未重置。");
    }

    public async Task<IActionResult> OnPostRetireUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = await adminUserService.RetireAsync(ActorUserId, userId, cancellationToken);
        await PublishAccountRevocationAsync(result);
        return UserMutationFeedback(result, "用户已退役。", "用户未退役。");
    }

    public async Task<IActionResult> OnPostCreateChannelAsync(string? name, bool isPrivate, CancellationToken cancellationToken)
    {
        var result = await conversationCommandService.CreateAsync(
            ActorUserId,
            new CreateConversationRequest(
                isPrivate ? ConversationType.PrivateChannel : ConversationType.PublicChannel,
                name),
            cancellationToken);
        return ConversationFeedback(result.Status, "频道已创建。", "频道未创建。");
    }

    public async Task<IActionResult> OnPostRenameChannelAsync(Guid conversationId, string? name, CancellationToken cancellationToken)
    {
        var result = await conversationCommandService.UpdateChannelAsync(
            ActorUserId,
            conversationId,
            new UpdateConversationRequest(name),
            cancellationToken);
        return ConversationFeedback(result.Status, "频道名称已更新。", "频道名称未更新。");
    }

    public async Task<IActionResult> OnPostDeleteChannelAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var result = await conversationCommandService.DeleteChannelAsync(ActorUserId, conversationId, cancellationToken);
        if (result.Status == ConversationOperationStatus.NoContent && result.RevokedUserIds is not null)
        {
            foreach (var userId in result.RevokedUserIds)
            {
                await serviceProvider.GetRequiredService<ConversationAccessRevokedPublisher>()
                    .TryPublishAsync(userId, conversationId);
            }
        }
        return ConversationFeedback(result.Status, "频道已删除。", "频道未删除。");
    }

    public async Task<IActionResult> OnPostAddMemberAsync(Guid conversationId, Guid userId, bool isChannelAdmin, CancellationToken cancellationToken)
    {
        var result = await conversationCommandService.UpsertMemberAsync(
            ActorUserId,
            conversationId,
            new UpsertConversationMemberRequest(
                userId,
                isChannelAdmin ? ConversationMemberRole.Administrator : ConversationMemberRole.Member),
            cancellationToken);
        return ConversationFeedback(result.Status, "私有频道成员已更新。", "私有频道成员未更新。");
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var result = await conversationCommandService.RemoveMemberWithResultAsync(ActorUserId, conversationId, userId, cancellationToken);
        if (result.Status == ConversationOperationStatus.NoContent && result.RemovedUserId is Guid removedUserId)
        {
            await serviceProvider.GetRequiredService<ConversationAccessRevokedPublisher>()
                .TryPublishAsync(removedUserId, conversationId);
        }
        return ConversationFeedback(result.Status, "私有频道成员已移除。", "私有频道成员未移除。");
    }

    public string FormatBytes(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:F1} KiB"
        : $"{bytes / 1024d / 1024d:F1} MiB";

    private Guid ActorUserId => Guid.ParseExact(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value, "D");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Status = await adminOperationsService.GetStatusAsync(cancellationToken);
        UploadLimitBytes = await uploadSettingsService.GetEffectiveMaximumFileBytesAsync(cancellationToken);
        Users = await adminUserService.ListUsersAsync(cancellationToken);
        ActiveUsers = Users.Where(user => !user.IsDisabled && user.RetiredAt is null).ToArray();
        Channels = await adminOperationsService.ListChannelsAsync(cancellationToken);
        foreach (var channel in Channels.Where(channel => channel.Type == ConversationType.PrivateChannel))
        {
            var result = await conversationQueryService.ListMembersAsync(ActorUserId, channel.Id, cancellationToken);
            if (result.Status == ConversationOperationStatus.Success && result.Value is not null)
            {
                Members[channel.Id] = result.Value.Members;
            }
        }
    }

    private async Task PublishAccountRevocationAsync(AdminUserMutationResult result)
    {
        if (result.RequiresAccessRevocation && result.User is not null && result.MinimumAccessTokenVersion is long version)
        {
            await serviceProvider.GetRequiredService<AccountAccessRevokedPublisher>()
                .TryPublishAsync(result.User.UserId, version);
        }
    }

    private IActionResult RedirectWithFeedback(string message, bool isError = false)
    {
        FeedbackMessage = message;
        FeedbackIsError = isError;
        return RedirectToPage();
    }

    private IActionResult UserMutationFeedback(
        AdminUserMutationResult result,
        string successMessage,
        string failurePrefix)
    {
        if (result.Status is AdminUserMutationStatus.Updated or
            AdminUserMutationStatus.PasswordReset or
            AdminUserMutationStatus.Retired)
        {
            return RedirectWithFeedback(successMessage);
        }

        if (result.Status == AdminUserMutationStatus.Unchanged)
        {
            return RedirectWithFeedback("无需更改。", isError: false);
        }

        var reason = result.Status switch
        {
            AdminUserMutationStatus.ValidationFailed => "密码不符合要求。",
            AdminUserMutationStatus.UserNotFound => "找不到用户。",
            AdminUserMutationStatus.UserRetired => "用户已退役。",
            AdminUserMutationStatus.SelfActionForbidden => "不能禁用或退役当前登录账号。",
            AdminUserMutationStatus.LastActiveAdministrator => "不能移除最后一个正常管理员。",
            _ => "当前账号已失去管理权限。",
        };
        return RedirectWithFeedback($"{failurePrefix}{reason}", isError: true);
    }

    private IActionResult ConversationFeedback(
        ConversationOperationStatus status,
        string successMessage,
        string failurePrefix)
    {
        if (status is ConversationOperationStatus.Created or
            ConversationOperationStatus.Success or
            ConversationOperationStatus.NoContent)
        {
            return RedirectWithFeedback(successMessage);
        }

        var reason = status switch
        {
            ConversationOperationStatus.InvalidRequest => "输入内容无效。",
            ConversationOperationStatus.AccessRevoked => "频道不存在或已删除。",
            ConversationOperationStatus.UserNotFound => "找不到用户。",
            ConversationOperationStatus.ConversationTypeConflict => "该频道类型不支持此操作。",
            _ => "当前账号已失去管理权限。",
        };
        return RedirectWithFeedback($"{failurePrefix}{reason}", isError: true);
    }
}
