using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

/// <summary>Ephemeral, server-authoritative state for the channel settings overlay.</summary>
public sealed partial class ChannelSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IClientSession _session;
    private readonly IPlatformInteractionService _platformInteractions;
    private readonly Func<long, Task> _viewChannel;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _emailCancellation;
    private CancellationTokenSource? _tabCancellation;
    private long _generation;
    private long _tabGeneration;
    private ChannelSettingsSnapshot? _snapshot;
    private ChannelDetails? _details;
    private bool _isSnapshotCurrent;
    private bool _isAuthorityCurrent;
    private bool _disposed;

    public ChannelSettingsViewModel(
        IClientSession session,
        IPlatformInteractionService platformInteractions,
        Func<long, Task> viewChannel)
    {
        _session = session;
        _platformInteractions = platformInteractions;
        _viewChannel = viewChannel;
        Members.CollectionChanged += OnMembersCollectionChanged;
    }

    public ObservableCollection<ChannelSettingsChannelItem> Channels { get; } = [];
    public ObservableCollection<ChannelFolder> Folders { get; } = [];
    public ObservableCollection<ChannelMemberItem> Members { get; } = [];
    public ObservableCollection<ChannelColorOption> ColorPalette { get; } =
    [
        new("#A47462"), new("#C2726A"), new("#E4523D"), new("#E7664D"), new("#EE7E4A"), new("#F4AE55"),
        new("#76CE90"), new("#53A063"), new("#94C849"), new("#BFD56F"), new("#FAE589"), new("#F5CE6E"),
        new("#A6DCBF"), new("#ADDFE5"), new("#A6C7E5"), new("#4F8DE4"), new("#95A5FD"), new("#B0A5FD"),
        new("#C2C2C2"), new("#C8BEBF"), new("#C6A8AD"), new("#E79AB5"), new("#BD86E5"), new("#9987E1")
    ];
    public ObservableCollection<ChannelPermissionItem> Permissions { get; } = [];
    public IReadOnlyList<string> ArchiveFilterOptions { get; } = ["未归档", "已归档", "全部"];
    public IReadOnlyList<string> SortOptions { get; } = ["名称", "订阅人数", "活跃度"];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsDetailLoading { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial ChannelSettingsListMode ListMode { get; set; } = ChannelSettingsListMode.Subscribed;
    [ObservableProperty] public partial ChannelSettingsArchiveFilter ArchiveFilter { get; set; } = ChannelSettingsArchiveFilter.Unarchived;
    [ObservableProperty] public partial ChannelSettingsSort Sort { get; set; } = ChannelSettingsSort.Name;
    [ObservableProperty] public partial int ArchiveFilterIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial ChannelSettingsChannelItem? SelectedChannel { get; set; }
    [ObservableProperty] public partial bool IsNarrow { get; set; }
    [ObservableProperty] public partial bool IsCompactSettingsHeader { get; set; }
    [ObservableProperty] public partial bool IsListVisibleOnNarrow { get; set; } = true;
    [ObservableProperty] public partial bool IsEditDialogOpen { get; set; }
    [ObservableProperty] public partial string EditLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditValue { get; set; } = string.Empty;
    [ObservableProperty] public partial ChannelSettingsEditKind EditKind { get; set; }
    [ObservableProperty] public partial string? EditError { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial long? DraftFolderId { get; set; }
    [ObservableProperty] public partial ChannelFolder? DraftFolder { get; set; }
    [ObservableProperty] public partial bool IsFolderDirty { get; set; }
    [ObservableProperty] public partial bool IsCreateFolderOpen { get; set; }
    [ObservableProperty] public partial string NewFolderName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewFolderDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial ChannelSettingsConfirmation Confirmation { get; set; }
    [ObservableProperty] public partial string? EmailAddress { get; set; }
    [ObservableProperty] public partial ChannelSettingsTab ActiveTab { get; set; }
    [ObservableProperty] public partial ChannelPersonalSettings? PersonalSettings { get; set; }
    [ObservableProperty] public partial string PersonalColor { get; set; } = string.Empty;
    [ObservableProperty] public partial string OriginalPersonalColor { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsColorPickerOpen { get; set; }
    [ObservableProperty] public partial bool IsCustomColorExpanded { get; set; }
    [ObservableProperty] public partial double ColorPickerAnchorX { get; set; } = 12d;
    [ObservableProperty] public partial double ColorPickerAnchorY { get; set; } = 68d;
    [ObservableProperty] public partial bool IsMemberDataCurrent { get; set; }
    [ObservableProperty] public partial bool IsTabLoading { get; set; }
    [ObservableProperty] public partial string MemberSearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool SendNewSubscriptionMessages { get; set; } = true;
    [ObservableProperty] public partial ChannelMemberItem? PendingMemberRemoval { get; set; }
    [ObservableProperty] public partial bool IsMemberRemovalConfirmationOpen { get; set; }
    [ObservableProperty] public partial bool IsCreateChannelOpen { get; set; }
    [ObservableProperty] public partial string NewChannelName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewChannelDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial int NewChannelPrivacyIndex { get; set; }
    [ObservableProperty] public partial bool NewChannelIsPrivate { get; set; }
    [ObservableProperty] public partial bool NewChannelHistoryPublic { get; set; }
    [ObservableProperty] public partial bool NewChannelIsDefault { get; set; }
    [ObservableProperty] public partial ChannelPermissionItem? SelectedPermission { get; set; }
    [ObservableProperty] public partial bool DraftIsPrivate { get; set; }
    [ObservableProperty] public partial bool DraftIsWebPublic { get; set; }
    [ObservableProperty] public partial bool DraftHistoryPublic { get; set; }
    [ObservableProperty] public partial bool DraftIsDefault { get; set; }
    [ObservableProperty] public partial ChannelTopicsPolicy DraftTopicsPolicy { get; set; }
    [ObservableProperty] public partial int RetentionMode { get; set; }
    [ObservableProperty] public partial string RetentionDaysText { get; set; } = string.Empty;
    [ObservableProperty] public partial ChannelUserGroup? SelectedNamedGroup { get; set; }
    public IReadOnlyList<ChannelTopicsPolicy> TopicsPolicyOptions { get; } = Enum.GetValues<ChannelTopicsPolicy>();
    public IReadOnlyList<string> RetentionOptions { get; } = ["继承 Realm", "永久保留", "保留天数"];
    public IReadOnlyList<string> NewChannelPrivacyOptions { get; } = ["公开", "私密"];
    public IEnumerable<ChannelUserGroup> NamedGroups => _snapshot?.UserGroups.Where(group => !group.IsDeactivated).OrderBy(group => group.Name, StringComparer.Ordinal) ?? Enumerable.Empty<ChannelUserGroup>();

    public IEnumerable<ChannelSettingsChannelItem> FilteredChannels => Channels
        .Where(MatchesMode)
        .Where(item => ArchiveFilter == ChannelSettingsArchiveFilter.All || item.IsArchived == (ArchiveFilter == ChannelSettingsArchiveFilter.Archived))
        .Where(item => string.IsNullOrWhiteSpace(SearchText) || item.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) || (item.Description?.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
        .OrderBy(item => Sort == ChannelSettingsSort.Name ? item.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(item => Sort == ChannelSettingsSort.Subscribers ? item.SubscriberCount ?? -1 : 0)
        .ThenByDescending(item => Sort == ChannelSettingsSort.Traffic ? item.WeeklyTraffic ?? -1 : 0)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    public ChannelDetails? Details => _details;
    public ChannelSettingsAccess Access => _details is null || _snapshot is null || SelectedChannel is null
        ? ChannelSettingsAccess.ReadOnly
        : ChannelPermissionEvaluator.Evaluate(_details, _snapshot, SelectedChannel.IsSubscribed);
    public bool HasDetails => _details is not null;
    public bool CanAdminister => _isAuthorityCurrent && Access.CanAdministerChannel && !IsBusy;
    public bool CanCreateFolder => _isAuthorityCurrent && _snapshot?.IsOrganizationAdministrator == true && !IsBusy;
    public bool CanFetchEmail => _isAuthorityCurrent && Access.CanSendMessages && !IsBusy;
    public bool CanArchive => _isAuthorityCurrent && !IsBusy && _snapshot?.IsOrganizationAdministrator == true && _details is { IsArchived: false };
    public bool CanUnarchive => _isAuthorityCurrent && !IsBusy && _snapshot?.IsOrganizationAdministrator == true && _details is { IsArchived: true };
    public bool CanChangeArchive => CanArchive || CanUnarchive;
    public bool CanUnsubscribe => _isAuthorityCurrent && SelectedChannel?.IsSubscribed == true && !IsBusy;
    public bool CanSubscribe => _isAuthorityCurrent && Access.CanSubscribe && !IsBusy;
    public bool CanChangeSubscription => CanUnsubscribe || CanSubscribe;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasEmailAddress => !string.IsNullOrWhiteSpace(EmailAddress);
    public bool IsDesktopListVisible => !IsNarrow;
    // The detail is deliberately shared between desktop and narrow layouts so
    // the four settings tabs cannot drift into two different implementations.
    public bool IsDesktopDetailVisible => !IsNarrow || !IsListVisibleOnNarrow;
    public bool IsNarrowListVisible => IsNarrow && IsListVisibleOnNarrow;
    public bool IsNarrowDetailVisible => false;
    public bool IsNarrowBackVisible => IsNarrow && !IsListVisibleOnNarrow;
    public bool IsConfirmationOpen => Confirmation != ChannelSettingsConfirmation.None;
    public bool CanSaveEdit => !IsBusy && IsEditDialogOpen && _details is not null && (EditKind switch
    {
        ChannelSettingsEditKind.Name => !string.IsNullOrWhiteSpace(EditValue) &&
            !string.Equals(EditValue.Trim(), _details.Name, StringComparison.Ordinal),
        ChannelSettingsEditKind.Description =>
            !string.Equals(EditValue.Trim(), _details.Description ?? string.Empty, StringComparison.Ordinal),
        _ => false
    });
    public bool CanSaveFolder => CanAdminister && IsFolderDirty;
    public bool CanCreateNewFolder => CanCreateFolder && !string.IsNullOrWhiteSpace(NewFolderName);
    public bool CanOpenCreateChannel => _isSnapshotCurrent && _snapshot?.IsOrganizationAdministrator == true && !IsBusy;
    public bool CanRefreshSettings => !IsBusy && !IsLoading;
    public bool CanSubmitCreateChannel => CanOpenCreateChannel && IsCreateChannelOpen && !string.IsNullOrWhiteSpace(NewChannelName);
    public bool CanShareNewChannelHistory => NewChannelIsPrivate;
    public bool CanSetNewChannelDefault => CanOpenCreateChannel && !NewChannelIsPrivate;
    public string NewChannelPrivacyDescription => NewChannelIsPrivate
        ? "需要相应权限才能查看和加入。"
        : "除访客外，所有人都可以查看和加入。";
    public bool CanViewMembers => _isAuthorityCurrent && SelectedChannel?.IsArchived != true && Access.HasMetadataAccess;
    public bool CanAddMembers => !IsBusy && IsMemberDataCurrent && _isAuthorityCurrent && SelectedChannel?.IsArchived != true && Access.CanAddSubscribers;
    public bool CanRemoveMembers => !IsBusy && IsMemberDataCurrent && _isAuthorityCurrent && SelectedChannel?.IsArchived != true && Access.CanRemoveSubscribers;
    public bool CanAddSelectedMembers => CanAddMembers && Members.Any(member => member.IsCandidate && member.IsSelected);
    public bool CanManageMembers => CanAddMembers || CanRemoveMembers;
    public bool CanChangePersonal => _isAuthorityCurrent && !IsBusy && SelectedChannel?.IsArchived != true && SelectedChannel?.IsSubscribed == true;
    public bool HasValidPersonalColor => TryNormalizePersonalColor(PersonalColor, out _);
    public string PersonalColorPreview => TryNormalizePersonalColor(PersonalColor, out var color) ? color : "#CBD5E1";
    public string PersonalColorPreviewSoft => BlendPersonalColorWithWhite(PersonalColorPreview);
    public string PersonalColorPreviewLabel => TryNormalizePersonalColor(PersonalColor, out var color) ? color : "无效颜色";
    public string? PersonalColorError => string.IsNullOrWhiteSpace(PersonalColor) || HasValidPersonalColor ? null : "请输入 #RRGGBB 格式的颜色。";
    public bool HasPersonalColorError => !string.IsNullOrWhiteSpace(PersonalColorError);
    public bool CanSavePersonalColor => CanChangePersonal && HasValidPersonalColor;
    public bool CanConfirmRemoveMember => IsMemberRemovalConfirmationOpen && CanRemoveMembers && PendingMemberRemoval is not null;
    public bool CanChangeContentAdvanced => CanAdminister && Access.HasContentAccess && SelectedChannel?.IsArchived != true;
    public bool CanChangeAdvanced => CanChangeContentAdvanced;
    public bool CanChangeWebPublic => CanChangeContentAdvanced && _snapshot?.IsOrganizationAdministrator == true;
    public bool CanChangeDefaultStream => _isAuthorityCurrent && !IsBusy && _snapshot?.IsOrganizationAdministrator == true && SelectedChannel?.IsArchived != true;
    public bool CanSetDraftDefault => CanChangeDefaultStream && !DraftIsPrivate;
    public bool CanSaveSelectedNamedGroup => CanChangeContentAdvanced &&
        SelectedPermission?.Value is NamedChannelGroupSetting current &&
        SelectedNamedGroup is { } selected && selected.GroupId != current.GroupId;
    public bool IsGeneralTab => ActiveTab == ChannelSettingsTab.General;
    public bool IsPersonalTab => ActiveTab == ChannelSettingsTab.Personal;
    public bool IsSubscribersTab => ActiveTab == ChannelSettingsTab.Subscribers;
    public bool IsPermissionsTab => ActiveTab == ChannelSettingsTab.Permissions;
    public bool IsSubscribedListMode => ListMode == ChannelSettingsListMode.Subscribed;
    public bool IsAvailableListMode => ListMode == ChannelSettingsListMode.Available;
    public bool IsAllListMode => ListMode == ChannelSettingsListMode.All;
    public IEnumerable<ChannelMemberItem> FilteredMembers => Members.Where(MatchesMemberSearch);
    public IEnumerable<ChannelMemberItem> SubscribedMembers => Members.Where(member => member.IsMember);
    public IEnumerable<ChannelMemberItem> CandidateMembers => FilteredMembers.Where(member => member.IsCandidate);
    public double MemberListHeight => ClampListHeight(SubscribedMembers.Count(), 72, 260);
    public double CandidateListHeight => ClampListHeight(CandidateMembers.Count(), 72, 220);
    public double PermissionListHeight => ClampListHeight(Permissions.Count, 120, 360);
    public string SelectedName => SelectedChannel?.Name ?? "频道";
    public string ChannelCountLabel => $"{FilteredChannels.Count()} 个频道";
    public string MemberCountLabel => $"{Members.Count(member => member.IsMember)} 位订阅者";
    public string MemberRemovalConfirmationText => PendingMemberRemoval is null
        ? "确定移除此订阅者吗？"
        : $"确定将“{PendingMemberRemoval.Name}”移出此频道吗？";
    public string? MemberManagementStatus => !CanViewMembers
        ? "你没有查看此频道订阅者的权限。"
        : !IsMemberDataCurrent
            ? IsTabLoading ? "正在加载成员与权限信息…" : "成员或权限信息尚未准备好，暂不能修改订阅者。"
            : !CanManageMembers ? "你没有添加或移出此频道订阅者的权限。" : null;
    public bool HasMemberManagementStatus => !string.IsNullOrWhiteSpace(MemberManagementStatus);
    public string ColorPickerPrivacyGlyph => _details?.IsPrivate == true ? "\uE72E" : "#";
    public string? ColorPickerPrivacyFontFamily => _details?.IsPrivate == true ? "Segoe Fluent Icons" : null;
    public string ColorPickerPreviewName => SelectedChannel?.Name ?? "频道";
    public bool HasMembers => Members.Any(member => member.IsMember);
    public string PrivacyLabel => _details?.IsPrivate == true ? "私有频道" : _details?.IsWebPublic == true ? "Web 公开频道" : "公开频道";
    public string DescriptionLabel => string.IsNullOrWhiteSpace(_details?.Description) ? "暂无频道说明" : _details!.Description!;
    public string CreatorLabel => _details?.CreatorId is { } id
        ? _session.State.Users.TryGetValue(id, out var user) ? $"由 {user.FullName} 创建" : $"创建者 ID: {id}"
        : "创建者信息不可用";
    public string DateLabel => _details?.DateCreated is { } date ? $"创建于 {date.LocalDateTime:yyyy年M月d日}" : "创建日期不可用";
    public string ChannelIdLabel => _details is { } details ? $"频道 ID: {details.ChannelId}" : string.Empty;
    public string FolderLabel => DraftFolderId is null ? "没有" : Folders.FirstOrDefault(folder => folder.FolderId == DraftFolderId)?.Name ?? "没有";
    public bool CanClearFolder => CanAdminister && DraftFolderId is not null;
    public string SubscriptionActionLabel => SelectedChannel?.IsSubscribed == true ? "退出" : "订阅";
    public string ArchiveActionLabel => SelectedChannel?.IsArchived == true ? "取消归档" : "归档";
    public string PersonalMutedLabel => PersonalSettings?.IsMuted == true ? "取消静音" : "静音";
    public string PersonalPinnedLabel => PersonalSettings?.IsPinned == true ? "取消置顶" : "置顶";
    public string PersonalMutedStatus => PersonalSettings?.IsMuted == true ? "已静音" : "未静音";
    public string PersonalPinnedStatus => PersonalSettings?.IsPinned == true ? "已置顶" : "未置顶";
    public string DesktopNotificationsLabel => NotificationLabel("桌面通知", PersonalSettings?.DesktopNotifications);
    public string AudibleNotificationsLabel => NotificationLabel("声音通知", PersonalSettings?.AudibleNotifications);
    public string PushNotificationsLabel => NotificationLabel("推送通知", PersonalSettings?.PushNotifications);
    public string EmailNotificationsLabel => NotificationLabel("邮件通知", PersonalSettings?.EmailNotifications);
    public string WildcardNotificationsLabel => NotificationLabel("通配符提及", PersonalSettings?.WildcardMentionsNotify);
    public string DesktopNotificationsStatus => NotificationStatus(PersonalSettings?.DesktopNotifications);
    public string AudibleNotificationsStatus => NotificationStatus(PersonalSettings?.AudibleNotifications);
    public string PushNotificationsStatus => NotificationStatus(PersonalSettings?.PushNotifications);
    public string EmailNotificationsStatus => NotificationStatus(PersonalSettings?.EmailNotifications);
    public string WildcardNotificationsStatus => NotificationStatus(PersonalSettings?.WildcardMentionsNotify);
    public string DesktopNotificationsAction => NotificationAction(PersonalSettings?.DesktopNotifications);
    public string AudibleNotificationsAction => NotificationAction(PersonalSettings?.AudibleNotifications);
    public string PushNotificationsAction => NotificationAction(PersonalSettings?.PushNotifications);
    public string EmailNotificationsAction => NotificationAction(PersonalSettings?.EmailNotifications);
    public string WildcardNotificationsAction => NotificationAction(PersonalSettings?.WildcardMentionsNotify);
    public GridLength ListPaneWidth => !IsNarrow ? new GridLength(400d) : IsListVisibleOnNarrow ? GridLength.Star : new GridLength(0d);
    public GridLength DetailPaneWidth => !IsNarrow ? GridLength.Star : IsListVisibleOnNarrow ? new GridLength(0d) : GridLength.Star;
    public string ConfirmationText => Confirmation switch
    {
        ChannelSettingsConfirmation.Unsubscribe => $"确定退出频道“{SelectedName}”吗？",
        ChannelSettingsConfirmation.Archive => $"确定归档频道“{SelectedName}”吗？归档后频道将不再活跃。",
        ChannelSettingsConfirmation.Unarchive => $"确定取消归档频道“{SelectedName}”吗？",
        _ => string.Empty
    };

    public async Task OpenAsync(long channelId)
    {
        if (channelId <= 0) return;
        IsOpen = true;
        IsListVisibleOnNarrow = !IsNarrow;
        await ReloadAsync(channelId);
    }

    public async Task OpenCreateAsync(long? channelId)
    {
        IsOpen = true;
        IsListVisibleOnNarrow = !IsNarrow;
        await ReloadAsync(channelId);
        if (!IsOpen) return;
        if (CanOpenCreateChannel) OpenCreateChannel();
        else if (!HasError) Error = "当前账户没有创建频道权限。";
    }

    [RelayCommand]
    public void Close()
    {
        CancelLoad();
        IsOpen = false;
        IsEditDialogOpen = false;
        IsCreateFolderOpen = false;
        IsCreateChannelOpen = false;
        IsColorPickerOpen = false;
        IsCustomColorExpanded = false;
        IsMemberRemovalConfirmationOpen = false;
        PendingMemberRemoval = null;
        Confirmation = ChannelSettingsConfirmation.None;
        _isSnapshotCurrent = false;
        _isAuthorityCurrent = false;
        IsMemberDataCurrent = false;
        EmailAddress = null;
        Error = null;
    }

    [RelayCommand]
    private void CloseTopLayer()
    {
        if (Confirmation != ChannelSettingsConfirmation.None) CancelConfirmation();
        else if (IsMemberRemovalConfirmationOpen) CancelRemoveMember();
        else if (IsColorPickerOpen) CancelPersonalColorPicker();
        else if (IsCreateChannelOpen) CancelCreateChannel();
        else if (IsCreateFolderOpen) CancelCreateFolder();
        else if (IsEditDialogOpen) CancelEdit();
        else Close();
    }

    public void UpdateViewport(double width)
    {
        IsCompactSettingsHeader = width <= 560d;
        var narrow = width <= 720d;
        if (IsNarrow != narrow)
        {
            IsNarrow = narrow;
            if (!narrow) IsListVisibleOnNarrow = false;
            else if (SelectedChannel is null) IsListVisibleOnNarrow = true;
        }
    }

    [RelayCommand]
    private void BackToList() => IsListVisibleOnNarrow = true;

    [RelayCommand] private void SetSubscribedMode() => ListMode = ChannelSettingsListMode.Subscribed;
    [RelayCommand] private void SetAvailableMode() => ListMode = ChannelSettingsListMode.Available;
    [RelayCommand] private void SetAllMode() => ListMode = ChannelSettingsListMode.All;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync() => await ReloadAsync(SelectedChannel?.ChannelId);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectChannelAsync(ChannelSettingsChannelItem? channel)
    {
        if (channel is null) return;
        IsListVisibleOnNarrow = false;
        await ReloadAsync(channel.ChannelId);
    }

    [RelayCommand]
    private void BeginEditName() => BeginEdit(ChannelSettingsEditKind.Name, "频道名称", _details?.Name ?? string.Empty);

    [RelayCommand]
    private void BeginEditDescription() => BeginEdit(ChannelSettingsEditKind.Description, "频道说明", _details?.Description ?? string.Empty);

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditDialogOpen = false;
        EditError = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveEditAsync()
    {
        if (!CanSaveEdit || _details is null) return;
        var targetId = _details.ChannelId;
        var value = EditValue.Trim();
        if (EditKind == ChannelSettingsEditKind.Name && !ValidateLength(value, _snapshot?.Limits.MaxChannelNameLength, "频道名称")) return;
        if (EditKind == ChannelSettingsEditKind.Description && !ValidateLength(value, _snapshot?.Limits.MaxChannelDescriptionLength, "频道说明", allowEmpty: true)) return;
        await ExecuteWriteAsync(async token =>
        {
            await _session.UpdateChannelAsync(targetId,
                EditKind == ChannelSettingsEditKind.Name ? value : null,
                EditKind == ChannelSettingsEditKind.Description ? value : null,
                null, false, token);
            if (!IsOpen) return;
            IsEditDialogOpen = false;
            await ReloadAsync(targetId);
        });
    }

    [RelayCommand]
    private void ChangeFolder(long? folderId)
    {
        DraftFolderId = folderId;
        IsFolderDirty = _details?.FolderId != folderId;
        OnPropertyChanged(nameof(FolderLabel));
    }

    [RelayCommand]
    private void ClearFolder() => ChangeFolder(null);

    [RelayCommand]
    private void CancelFolder()
    {
        DraftFolderId = _details?.FolderId;
        IsFolderDirty = false;
        OnPropertyChanged(nameof(FolderLabel));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveFolderAsync()
    {
        if (!CanSaveFolder || _details is null) return;
        var targetId = _details.ChannelId;
        var folderId = DraftFolderId;
        await ExecuteWriteAsync(async token =>
        {
            await _session.UpdateChannelAsync(targetId, null, null, folderId, folderId is null, token);
            if (IsOpen) await ReloadAsync(targetId);
        });
    }

    [RelayCommand]
    private void OpenCreateFolder()
    {
        if (!CanCreateFolder) return;
        NewFolderName = string.Empty;
        NewFolderDescription = string.Empty;
        EditError = null;
        IsCreateFolderOpen = true;
    }

    [RelayCommand]
    private void CancelCreateFolder()
    {
        IsCreateFolderOpen = false;
        EditError = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CreateFolderAsync()
    {
        var targetId = _details?.ChannelId;
        var name = NewFolderName.Trim();
        if (!CanCreateFolder || !ValidateLength(name, _snapshot?.Limits.MaxChannelFolderNameLength, "文件夹名称")) return;
        if (!ValidateLength(NewFolderDescription.Trim(), _snapshot?.Limits.MaxChannelFolderDescriptionLength, "文件夹说明", allowEmpty: true)) return;
        await ExecuteWriteAsync(async token =>
        {
            var folder = await _session.CreateChannelFolderAsync(name, NewFolderDescription.Trim(), token);
            if (!IsOpen) return;
            IsCreateFolderOpen = false;
            await ReloadAsync(targetId);
            if (IsOpen && SelectedChannel?.ChannelId == targetId) ChangeFolder(folder.FolderId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task FetchEmailAsync()
    {
        if (!CanFetchEmail || _details is null || _loadCancellation is not { } loadCancellation) return;
        CancelEmail();
        var targetId = _details.ChannelId;
        var generation = _generation;
        var emailCancellation = _emailCancellation = CancellationTokenSource.CreateLinkedTokenSource(loadCancellation.Token);
        Error = null;
        IsBusy = true;
        try
        {
            var emailAddress = await _session.GetChannelEmailAddressAsync(targetId, emailCancellation.Token);
            if (ReferenceEquals(_emailCancellation, emailCancellation) &&
                IsCurrent(generation, loadCancellation) &&
                SelectedChannel?.ChannelId == targetId)
                EmailAddress = emailAddress;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (ReferenceEquals(_emailCancellation, emailCancellation) &&
                IsCurrent(generation, loadCancellation) &&
                SelectedChannel?.ChannelId == targetId)
                Error = "无法获取频道邮件地址。";
        }
        finally
        {
            if (ReferenceEquals(_emailCancellation, emailCancellation)) _emailCancellation = null;
            emailCancellation.Dispose();
            IsBusy = false;
            NotifyAccess();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CopyEmailAsync()
    {
        if (string.IsNullOrWhiteSpace(EmailAddress)) return;
        try { await _platformInteractions.CopyTextAsync(EmailAddress); }
        catch { Error = "无法复制频道邮件地址。"; }
    }

    [RelayCommand]
    private void RequestUnsubscribe() { if (CanUnsubscribe) Confirmation = ChannelSettingsConfirmation.Unsubscribe; }

    [RelayCommand]
    private void RequestArchive()
    {
        if (CanArchive) Confirmation = ChannelSettingsConfirmation.Archive;
        else if (CanUnarchive) Confirmation = ChannelSettingsConfirmation.Unarchive;
    }

    [RelayCommand]
    private void CancelConfirmation() => Confirmation = ChannelSettingsConfirmation.None;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmAsync()
    {
        if (SelectedChannel is null || Confirmation == ChannelSettingsConfirmation.None) return;
        var targetId = SelectedChannel.ChannelId;
        var action = Confirmation;
        if (action == ChannelSettingsConfirmation.Archive && !CanArchive) return;
        if (action == ChannelSettingsConfirmation.Unarchive && !CanUnarchive) return;
        await ExecuteWriteAsync(async token =>
        {
            if (action == ChannelSettingsConfirmation.Unsubscribe) await _session.UnsubscribeChannelAsync(targetId, token);
            else if (action == ChannelSettingsConfirmation.Archive) await _session.ArchiveChannelAsync(targetId, token);
            else await _session.UnarchiveChannelAsync(targetId, token);
            if (!IsOpen) return;
            Confirmation = ChannelSettingsConfirmation.None;
            await ReloadAsync(targetId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SubscribeAsync()
    {
        if (!CanSubscribe || SelectedChannel is null) return;
        var targetId = SelectedChannel.ChannelId;
        await ExecuteWriteAsync(async token =>
        {
            await _session.SubscribeToChannelAsync(targetId, token);
            if (IsOpen) await ReloadAsync(targetId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ChangeSubscriptionAsync()
    {
        if (CanUnsubscribe) RequestUnsubscribe();
        else if (CanSubscribe) await SubscribeAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ChangeChannelSubscriptionAsync(ChannelSettingsChannelItem? channel)
    {
        if (channel is null) return;
        await SelectChannelAsync(channel);
        if (SelectedChannel?.ChannelId != channel.ChannelId) return;
        if (CanUnsubscribe) RequestUnsubscribe();
        else if (CanSubscribe) await SubscribeAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ViewChannelAsync()
    {
        if (SelectedChannel is { } channel) await _viewChannel(channel.ChannelId);
    }

    [RelayCommand]
    private async Task SelectTabAsync(string? tabName)
    {
        if (!Enum.TryParse<ChannelSettingsTab>(tabName, out var tab)) return;
        if (tab == ChannelSettingsTab.Personal && !CanChangePersonal) return;
        if (tab == ChannelSettingsTab.Subscribers && !CanViewMembers) return;
        CancelTabLoad();
        ActiveTab = tab;
        await LoadActiveTabAsync();
    }

    [RelayCommand]
    private void OpenCreateChannel()
    {
        if (!CanOpenCreateChannel) return;
        NewChannelName = NewChannelDescription = string.Empty;
        NewChannelPrivacyIndex = 0;
        NewChannelIsPrivate = false;
        NewChannelHistoryPublic = true;
        NewChannelIsDefault = false;
        IsCreateChannelOpen = true;
    }

    [RelayCommand] private void CancelCreateChannel() => IsCreateChannelOpen = false;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CreateChannelAsync()
    {
        if (!CanSubmitCreateChannel) return;
        var options = new ChannelCreateOptions(NewChannelName.Trim(), NewChannelDescription.Trim(), NewChannelIsPrivate, false, !NewChannelIsPrivate || NewChannelHistoryPublic, NewChannelIsDefault);
        await ExecuteWriteAsync(async token =>
        {
            var channel = await _session.CreateChannelAsync(options, token);
            if (!IsOpen) return;
            IsCreateChannelOpen = false;
            await ReloadAsync(channel.ChannelId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SetPersonalSettingAsync(string? settingName)
    {
        if (!CanChangePersonal || SelectedChannel is null || PersonalSettings is null || !Enum.TryParse<ChannelPersonalSetting>(settingName, out var setting)) return;
        var targetId = SelectedChannel.ChannelId;
        var next = setting switch
        {
            ChannelPersonalSetting.Muted => !PersonalSettings.IsMuted,
            ChannelPersonalSetting.Pinned => !PersonalSettings.IsPinned,
            ChannelPersonalSetting.DesktopNotifications => !(PersonalSettings.DesktopNotifications ?? false),
            ChannelPersonalSetting.AudibleNotifications => !(PersonalSettings.AudibleNotifications ?? false),
            ChannelPersonalSetting.PushNotifications => !(PersonalSettings.PushNotifications ?? false),
            ChannelPersonalSetting.EmailNotifications => !(PersonalSettings.EmailNotifications ?? false),
            _ => !(PersonalSettings.WildcardMentionsNotify ?? false)
        };
        await ExecuteWriteAsync(async token =>
        {
            await _session.SetChannelPersonalSettingAsync(targetId, new ChannelPersonalSettingChange(setting, BooleanValue: next), token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId) await ReloadAsync(targetId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SavePersonalColorAsync()
    {
        if (!CanSavePersonalColor || SelectedChannel is null || !TryNormalizePersonalColor(PersonalColor, out var color)) return;
        var targetId = SelectedChannel.ChannelId;
        await ExecuteWriteAsync(async token =>
        {
            await _session.SetChannelPersonalSettingAsync(targetId, new ChannelPersonalSettingChange(ChannelPersonalSetting.Color, ColorValue: color), token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId)
            {
                IsColorPickerOpen = false;
                IsCustomColorExpanded = false;
                await ReloadAsync(targetId);
            }
        });
    }

    [RelayCommand]
    private void OpenPersonalColorPicker()
    {
        if (!CanChangePersonal) return;
        OriginalPersonalColor = PersonalColor;
        IsCustomColorExpanded = false;
        IsColorPickerOpen = true;
    }

    [RelayCommand]
    private void CancelPersonalColorPicker()
    {
        PersonalColor = OriginalPersonalColor;
        IsCustomColorExpanded = false;
        IsColorPickerOpen = false;
    }

    [RelayCommand]
    private void ToggleCustomColor() => IsCustomColorExpanded = !IsCustomColorExpanded;

    [RelayCommand]
    private void SelectPersonalColor(string? color)
    {
        if (!TryNormalizePersonalColor(color, out var normalized)) return;
        PersonalColor = normalized;
    }

    [RelayCommand]
    private void RequestRemoveMember(ChannelMemberItem? member)
    {
        if (member is null || !member.IsMember || !CanRemoveMembers) return;
        PendingMemberRemoval = member;
        IsMemberRemovalConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelRemoveMember()
    {
        IsMemberRemovalConfirmationOpen = false;
        PendingMemberRemoval = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmRemoveMemberAsync()
    {
        if (!CanRemoveMembers || PendingMemberRemoval is null || SelectedChannel is null) return;
        var targetId = SelectedChannel.ChannelId; var userId = PendingMemberRemoval.UserId;
        await ExecuteWriteAsync(async token =>
        {
            await _session.RemoveChannelMembersAsync(targetId, [userId], token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId)
            {
                IsMemberRemovalConfirmationOpen = false;
                PendingMemberRemoval = null;
                await ReloadAsync(targetId);
            }
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task AddSelectedMembersAsync()
    {
        if (!CanAddMembers || SelectedChannel is null) return;
        var ids = Members.Where(member => !member.IsMember && member.IsSelected).Select(member => member.UserId).ToArray();
        if (ids.Length == 0) return;
        var targetId = SelectedChannel.ChannelId;
        await ExecuteWriteAsync(async token =>
        {
            await _session.AddChannelMembersAsync(targetId, ids, SendNewSubscriptionMessages, token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId) await ReloadAsync(targetId);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task UnarchiveAsync()
    {
        if (CanUnarchive) Confirmation = ChannelSettingsConfirmation.Unarchive;
        await Task.CompletedTask;
    }

    private async Task LoadActiveTabAsync()
    {
        if (SelectedChannel is null || !_isAuthorityCurrent || _loadCancellation is not { } rootCancellation) return;
        if (ActiveTab == ChannelSettingsTab.Personal && !CanChangePersonal) return;
        if (ActiveTab == ChannelSettingsTab.Subscribers && !CanViewMembers) return;
        CancelTabLoad();
        var targetId = SelectedChannel.ChannelId;
        var generation = _generation;
        var requestedTab = ActiveTab;
        var tabGeneration = ++_tabGeneration;
        var cancellation = _tabCancellation = CancellationTokenSource.CreateLinkedTokenSource(rootCancellation.Token);
        IsTabLoading = true;
        Error = null;
        try
        {
            if (requestedTab == ChannelSettingsTab.Personal)
            {
                var settings = await _session.GetChannelPersonalSettingsAsync(targetId, cancellation.Token);
                if (!IsCurrentTab(generation, rootCancellation, requestedTab, tabGeneration, cancellation, targetId)) return;
                PersonalSettings = settings;
                PersonalColor = settings.Color ?? string.Empty;
            }
            else if (requestedTab == ChannelSettingsTab.Subscribers)
            {
                IsMemberDataCurrent = false;
                Members.Clear();
                NotifyMemberProjection();
                var memberIdsTask = _session.GetChannelMemberIdsAsync(targetId, cancellation.Token);
                var realmUsersTask = _session.GetRealmUsersAsync(cancellation.Token);
                await Task.WhenAll(memberIdsTask, realmUsersTask);
                if (!IsCurrentTab(generation, rootCancellation, requestedTab, tabGeneration, cancellation, targetId)) return;
                var memberIds = await memberIdsTask;
                var users = await realmUsersTask;
                var usersById = users.ToDictionary(user => user.UserId);
                if (memberIds.Any(id => !usersById.ContainsKey(id)))
                {
                    Error = "成员信息不完整，无法安全管理订阅者。";
                    return;
                }

                foreach (var user in users.OrderBy(user => user.FullName, StringComparer.Ordinal))
                    Members.Add(new ChannelMemberItem(user.UserId, user.FullName, memberIds.Contains(user.UserId), user.Email, user.IsActive, user.IsBot));
                IsMemberDataCurrent = true;
                NotifyMemberProjection();
            }
            else if (requestedTab == ChannelSettingsTab.Permissions)
            {
                if (!IsCurrentTab(generation, rootCancellation, requestedTab, tabGeneration, cancellation, targetId)) return;
                ProjectPermissions();
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (IsCurrentTab(generation, rootCancellation, requestedTab, tabGeneration, cancellation, targetId))
                Error = "无法加载此设置页；当前内容保持只读。";
        }
        finally
        {
            if (IsCurrentTab(generation, rootCancellation, requestedTab, tabGeneration, cancellation, targetId))
                IsTabLoading = false;
            if (ReferenceEquals(_tabCancellation, cancellation)) _tabCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ProjectPermissions()
    {
        Permissions.Clear();
        if (_details is null) return;
        var source = new (ChannelGroupSettingName, ChannelGroupSetting?, string)[]
        {
            (ChannelGroupSettingName.CanSubscribe, _details.CanSubscribeGroup, "谁可以订阅"), (ChannelGroupSettingName.CanAddSubscribers, _details.CanAddSubscribersGroup, "谁可以添加订阅者"), (ChannelGroupSettingName.CanRemoveSubscribers, _details.CanRemoveSubscribersGroup, "谁可以移除订阅者"), (ChannelGroupSettingName.CanAdministerChannel, _details.CanAdministerChannelGroup, "谁可以管理频道"), (ChannelGroupSettingName.CanSendMessage, _details.CanSendMessageGroup, "谁可以发送消息"), (ChannelGroupSettingName.CanCreateTopic, _details.CanCreateTopicGroup, "谁可以创建话题"), (ChannelGroupSettingName.CanMoveMessagesWithinChannel, _details.CanMoveMessagesWithinChannelGroup, "谁可以在频道内移动消息"), (ChannelGroupSettingName.CanMoveMessagesOutOfChannel, _details.CanMoveMessagesOutOfChannelGroup, "谁可以移出消息"), (ChannelGroupSettingName.CanResolveTopics, _details.CanResolveTopicsGroup, "谁可以解决话题"), (ChannelGroupSettingName.CanDeleteAnyMessage, _details.CanDeleteAnyMessageGroup, "谁可以删除任意消息"), (ChannelGroupSettingName.CanDeleteOwnMessage, _details.CanDeleteOwnMessageGroup, "谁可以删除自己的消息")
        };
        foreach (var item in source) Permissions.Add(new ChannelPermissionItem(item.Item1, item.Item2, item.Item3));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveAdvancedAsync()
    {
        if (!CanChangeContentAdvanced || _details is null || SelectedChannel is null) return;
        if (!DraftIsPrivate && !DraftHistoryPublic) { Error = "公开或 Web 公开频道必须共享历史消息。"; return; }
        if (DraftIsPrivate && DraftIsWebPublic) { Error = "私有频道不能同时设为 Web 公开。"; return; }
        var retention = RetentionMode switch { 0 => ChannelRetentionPolicy.RealmDefault, 1 => ChannelRetentionPolicy.Unlimited, 2 when int.TryParse(RetentionDaysText, out var days) && days > 0 => ChannelRetentionPolicy.ForDays(days), _ => null };
        if (retention is null) { Error = "保留天数必须是正整数。"; return; }
        var currentRetention = _details.MessageRetentionDays switch
        {
            null => ChannelRetentionPolicy.RealmDefault,
            0 => ChannelRetentionPolicy.Unlimited,
            var days => ChannelRetentionPolicy.ForDays(days.Value)
        };
        var targetId = SelectedChannel.ChannelId;
        var changed = new ChannelAdvancedSettingsChange(
            IsPrivate: DraftIsPrivate != _details.IsPrivate ? DraftIsPrivate : null,
            IsWebPublic: CanChangeWebPublic && DraftIsWebPublic != _details.IsWebPublic ? DraftIsWebPublic : null,
            HistoryPublicToSubscribers: DraftHistoryPublic != _details.HistoryPublicToSubscribers ? DraftHistoryPublic : null,
            IsDefaultStream: CanChangeDefaultStream && DraftIsDefault != _details.IsDefaultStream ? DraftIsDefault : null,
            TopicsPolicy: DraftTopicsPolicy != (_details.TopicsPolicy ?? ChannelTopicsPolicy.Inherit) ? DraftTopicsPolicy : null,
            RetentionPolicy: retention != currentRetention ? retention : null);
        if (changed == new ChannelAdvancedSettingsChange()) return;
        await ExecuteWriteAsync(async token =>
        {
            await _session.UpdateChannelAdvancedSettingsAsync(targetId, changed, token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId) await ReloadAsync(targetId);
        });
    }

    [RelayCommand]
    private void SelectPermission(ChannelPermissionItem? permission)
    {
        SelectedPermission = permission;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveNamedGroupAsync()
    {
        if (!CanChangeContentAdvanced || !CanSaveSelectedNamedGroup || SelectedPermission is null || SelectedNamedGroup is null || SelectedChannel is null) return;
        if (SelectedPermission.Value is not NamedChannelGroupSetting oldGroup) return;
        var targetId = SelectedChannel.ChannelId;
        var newGroup = new NamedChannelGroupSetting(SelectedNamedGroup.GroupId);
        await ExecuteWriteAsync(async token =>
        {
            await _session.UpdateChannelAdvancedSettingsAsync(targetId, new ChannelAdvancedSettingsChange(GroupSetting: SelectedPermission.Name, NewGroup: newGroup, OldGroup: oldGroup), token);
            if (IsOpen && SelectedChannel?.ChannelId == targetId) await ReloadAsync(targetId);
        });
    }

    private async Task ReloadAsync(long? desiredChannelId)
    {
        CancelLoad();
        IsColorPickerOpen = false;
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var generation = ++_generation;
        IsLoading = true;
        _isSnapshotCurrent = false;
        _isAuthorityCurrent = false;
        IsMemberDataCurrent = false;
        NotifyAccess();
        Error = null;
        try
        {
            _snapshot = await _session.LoadChannelSettingsSnapshotAsync(cancellation.Token);
            if (!IsCurrent(generation, cancellation)) return;
            _isSnapshotCurrent = true;
            ReconcileChannels(_snapshot.Channels);
            ReconcileFolders(_snapshot.Folders);
            NotifyAccess();
            var selected = Channels.FirstOrDefault(item => item.ChannelId == desiredChannelId) ?? FilteredChannels.FirstOrDefault();
            SelectedChannel = selected;
            if (selected is not null) await LoadDetailsAsync(selected.ChannelId, refreshSnapshot: false, generation, cancellation);
            else ClearDetails();
        }
        catch (OperationCanceledException) { }
        catch { if (IsCurrent(generation, cancellation)) Error = "无法加载频道设置；当前页面为只读。"; }
        finally { if (IsCurrent(generation, cancellation)) { IsLoading = false; NotifyAccess(); } }
    }

    private async Task LoadDetailsAsync(long channelId, bool refreshSnapshot, long? expectedGeneration = null, CancellationTokenSource? expectedCancellation = null)
    {
        if (refreshSnapshot) { await ReloadAsync(channelId); return; }
        var cancellation = expectedCancellation ?? _loadCancellation;
        if (cancellation is null) return;
        var generation = expectedGeneration ?? _generation;
        IsDetailLoading = true;
        _details = null;
        EmailAddress = null;
        NotifyDetails();
        try
        {
            var details = await _session.LoadChannelDetailsAsync(channelId, cancellation.Token);
            if (!IsCurrent(generation, cancellation) || SelectedChannel?.ChannelId != channelId) return;
            _details = details;
            DraftIsPrivate = details.IsPrivate;
            DraftIsWebPublic = details.IsWebPublic;
            DraftHistoryPublic = details.IsPrivate ? details.HistoryPublicToSubscribers : true;
            DraftIsDefault = details.IsDefaultStream;
            DraftTopicsPolicy = details.TopicsPolicy ?? ChannelTopicsPolicy.Inherit;
            RetentionMode = details.MessageRetentionDays is null ? 0 : details.MessageRetentionDays == 0 ? 1 : 2;
            RetentionDaysText = details.MessageRetentionDays is > 0 ? details.MessageRetentionDays.Value.ToString() : string.Empty;
            DraftFolderId = details.FolderId;
            IsFolderDirty = false;
            _isAuthorityCurrent = true;
            await LoadActiveTabAsync();
        }
        catch (OperationCanceledException) { }
        catch { if (IsCurrent(generation, cancellation)) Error = "无法加载频道详情；当前页面为只读。"; }
        finally { if (IsCurrent(generation, cancellation)) { IsDetailLoading = false; NotifyDetails(); } }
    }

    private async Task ExecuteWriteAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        NotifyAccess();
        try { await action(CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch { Error = "操作失败；不会自动重试。"; }
        finally { IsBusy = false; NotifyAccess(); }
    }

    private void BeginEdit(ChannelSettingsEditKind kind, string label, string value)
    {
        if (!CanAdminister) return;
        EditKind = kind;
        EditLabel = label;
        EditValue = value;
        EditError = null;
        IsEditDialogOpen = true;
    }

    private bool ValidateLength(string value, int? maximum, string label, bool allowEmpty = false)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value)) { EditError = $"{label}不能为空。"; return false; }
        if (maximum is { } max && value.Length > max) { EditError = $"{label}不能超过 {max} 个字符。"; return false; }
        return true;
    }

    private bool MatchesMode(ChannelSettingsChannelItem item) => ListMode switch
    {
        ChannelSettingsListMode.Subscribed => item.IsSubscribed,
        ChannelSettingsListMode.Available => !item.IsSubscribed,
        _ => true
    };

    private static string NotificationLabel(string name, bool? value) => value switch
    {
        null => $"{name}：继承",
        true => $"{name}：开启",
        false => $"{name}：关闭"
    };

    private static string NotificationStatus(bool? value) => value switch
    {
        null => "继承组织默认",
        true => "已开启",
        false => "已关闭"
    };

    private static string NotificationAction(bool? value) => value == true ? "关闭" : "开启";

    private static bool TryNormalizePersonalColor(string? value, out string color)
    {
        color = string.Empty;
        if (value is null) return false;
        var trimmed = value.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#') return false;
        for (var index = 1; index < trimmed.Length; index++)
        {
            if (!Uri.IsHexDigit(trimmed[index])) return false;
        }

        color = trimmed.ToUpperInvariant();
        return true;
    }

    private static string BlendPersonalColorWithWhite(string color)
    {
        if (!TryNormalizePersonalColor(color, out var normalized)) return "#EEF2F7";
        var red = Convert.ToByte(normalized.Substring(1, 2), 16);
        var green = Convert.ToByte(normalized.Substring(3, 2), 16);
        var blue = Convert.ToByte(normalized.Substring(5, 2), 16);
        static byte Blend(byte value) => (byte)Math.Round((value * 0.22d) + (255d * 0.78d));
        return $"#{Blend(red):X2}{Blend(green):X2}{Blend(blue):X2}";
    }

    private bool MatchesMemberSearch(ChannelMemberItem member)
    {
        if (string.IsNullOrWhiteSpace(MemberSearchText)) return true;
        var search = MemberSearchText.Trim();
        return member.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (member.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void ReconcileChannels(IEnumerable<ChannelSummary> channels)
    {
        var items = channels.Select(summary => new ChannelSettingsChannelItem(summary)).ToArray();
        Reconcile(Channels, items, item => item.ChannelId);
        OnPropertyChanged(nameof(FilteredChannels));
    }

    private void ReconcileFolders(IEnumerable<ChannelFolder> folders) => Reconcile(
        Folders,
        folders.Where(static folder => !folder.IsArchived).OrderBy(static folder => folder.Order).ThenBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase),
        folder => folder.FolderId);

    private static void Reconcile<T, TKey>(ObservableCollection<T> destination, IEnumerable<T> desired, Func<T, TKey> key)
        where TKey : notnull
    {
        destination.Clear();
        foreach (var item in desired) destination.Add(item);
    }

    private static double ClampListHeight(int count, double minimum, double maximum) => Math.Clamp(Math.Max(1, count) * 42d, minimum, maximum);

    private void NotifyMemberProjection()
    {
        OnPropertyChanged(nameof(FilteredMembers));
        OnPropertyChanged(nameof(SubscribedMembers));
        OnPropertyChanged(nameof(CandidateMembers));
        OnPropertyChanged(nameof(MemberListHeight));
        OnPropertyChanged(nameof(CandidateListHeight));
        OnPropertyChanged(nameof(MemberCountLabel));
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(CanAddSelectedMembers));
    }

    private void OnMembersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
        {
            foreach (var item in eventArgs.OldItems.OfType<ChannelMemberItem>()) item.PropertyChanged -= OnMemberPropertyChanged;
        }

        if (eventArgs.NewItems is not null)
        {
            foreach (var item in eventArgs.NewItems.OfType<ChannelMemberItem>()) item.PropertyChanged += OnMemberPropertyChanged;
        }
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ChannelMemberItem.IsSelected))
            OnPropertyChanged(nameof(CanAddSelectedMembers));
    }

    private void ClearDetails()
    {
        _details = null;
        DraftFolderId = null;
        IsFolderDirty = false;
        NotifyDetails();
    }

    private void NotifyDetails()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(PrivacyLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CreatorLabel));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(ChannelIdLabel));
        OnPropertyChanged(nameof(ColorPickerPrivacyGlyph));
        OnPropertyChanged(nameof(ColorPickerPrivacyFontFamily));
        OnPropertyChanged(nameof(ColorPickerPreviewName));
        OnPropertyChanged(nameof(FolderLabel));
        OnPropertyChanged(nameof(CanSaveEdit));
        NotifyAccess();
    }

    private void NotifyAccess()
    {
        OnPropertyChanged(nameof(Access));
        OnPropertyChanged(nameof(CanAdminister));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanFetchEmail));
        OnPropertyChanged(nameof(CanArchive));
        OnPropertyChanged(nameof(CanUnarchive));
        OnPropertyChanged(nameof(CanChangeArchive));
        OnPropertyChanged(nameof(ArchiveActionLabel));
        OnPropertyChanged(nameof(CanUnsubscribe));
        OnPropertyChanged(nameof(CanSubscribe));
        OnPropertyChanged(nameof(CanChangeSubscription));
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        OnPropertyChanged(nameof(CanSaveFolder));
        OnPropertyChanged(nameof(CanClearFolder));
        OnPropertyChanged(nameof(CanCreateNewFolder));
        OnPropertyChanged(nameof(CanOpenCreateChannel));
        OnPropertyChanged(nameof(CanSetNewChannelDefault));
        OnPropertyChanged(nameof(CanRefreshSettings));
        OnPropertyChanged(nameof(CanSubmitCreateChannel));
        OnPropertyChanged(nameof(CanViewMembers));
        OnPropertyChanged(nameof(CanAddMembers));
        OnPropertyChanged(nameof(CanRemoveMembers));
        OnPropertyChanged(nameof(CanConfirmRemoveMember));
        OnPropertyChanged(nameof(CanAddSelectedMembers));
        OnPropertyChanged(nameof(CanManageMembers));
        OnPropertyChanged(nameof(MemberManagementStatus));
        OnPropertyChanged(nameof(HasMemberManagementStatus));
        OnPropertyChanged(nameof(CanChangePersonal));
        OnPropertyChanged(nameof(CanChangeAdvanced));
        OnPropertyChanged(nameof(CanChangeContentAdvanced));
        OnPropertyChanged(nameof(CanChangeWebPublic));
        OnPropertyChanged(nameof(CanChangeDefaultStream));
        OnPropertyChanged(nameof(CanSetDraftDefault));
        OnPropertyChanged(nameof(CanSaveSelectedNamedGroup));
    }

    partial void OnSearchTextChanged(string value) { OnPropertyChanged(nameof(FilteredChannels)); OnPropertyChanged(nameof(ChannelCountLabel)); }
    partial void OnListModeChanged(ChannelSettingsListMode value)
    {
        OnPropertyChanged(nameof(FilteredChannels));
        OnPropertyChanged(nameof(ChannelCountLabel));
        OnPropertyChanged(nameof(IsSubscribedListMode));
        OnPropertyChanged(nameof(IsAvailableListMode));
        OnPropertyChanged(nameof(IsAllListMode));
    }
    partial void OnArchiveFilterChanged(ChannelSettingsArchiveFilter value) => OnPropertyChanged(nameof(FilteredChannels));
    partial void OnSortChanged(ChannelSettingsSort value) => OnPropertyChanged(nameof(FilteredChannels));
    partial void OnArchiveFilterIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(ChannelSettingsArchiveFilter), value)) ArchiveFilter = (ChannelSettingsArchiveFilter)value;
    }
    partial void OnSortIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(ChannelSettingsSort), value)) Sort = (ChannelSettingsSort)value;
    }
    partial void OnSelectedChannelChanged(ChannelSettingsChannelItem? value)
    {
        foreach (var channel in Channels) channel.IsSelected = channel.ChannelId == value?.ChannelId;
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(ColorPickerPreviewName));
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        NotifyAccess();
    }
    partial void OnDraftFolderIdChanged(long? value)
    {
        if (DraftFolder?.FolderId != value)
            DraftFolder = Folders.FirstOrDefault(folder => folder.FolderId == value);
        IsFolderDirty = _details?.FolderId != value;
        OnPropertyChanged(nameof(FolderLabel));
        OnPropertyChanged(nameof(CanClearFolder));
    }
    partial void OnDraftFolderChanged(ChannelFolder? value)
    {
        if (DraftFolderId != value?.FolderId) DraftFolderId = value?.FolderId;
    }
    partial void OnIsFolderDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSaveFolder));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanSaveEdit)); NotifyAccess(); }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanRefreshSettings));
    partial void OnEditValueChanged(string value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnEditKindChanged(ChannelSettingsEditKind value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnIsEditDialogOpenChanged(bool value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnEmailAddressChanged(string? value) => OnPropertyChanged(nameof(HasEmailAddress));
    partial void OnPersonalSettingsChanged(ChannelPersonalSettings? value)
    {
        OnPropertyChanged(nameof(PersonalMutedLabel));
        OnPropertyChanged(nameof(PersonalPinnedLabel));
        OnPropertyChanged(nameof(PersonalMutedStatus));
        OnPropertyChanged(nameof(PersonalPinnedStatus));
        OnPropertyChanged(nameof(DesktopNotificationsLabel));
        OnPropertyChanged(nameof(AudibleNotificationsLabel));
        OnPropertyChanged(nameof(PushNotificationsLabel));
        OnPropertyChanged(nameof(EmailNotificationsLabel));
        OnPropertyChanged(nameof(WildcardNotificationsLabel));
        OnPropertyChanged(nameof(DesktopNotificationsStatus));
        OnPropertyChanged(nameof(AudibleNotificationsStatus));
        OnPropertyChanged(nameof(PushNotificationsStatus));
        OnPropertyChanged(nameof(EmailNotificationsStatus));
        OnPropertyChanged(nameof(WildcardNotificationsStatus));
        OnPropertyChanged(nameof(DesktopNotificationsAction));
        OnPropertyChanged(nameof(AudibleNotificationsAction));
        OnPropertyChanged(nameof(PushNotificationsAction));
        OnPropertyChanged(nameof(EmailNotificationsAction));
        OnPropertyChanged(nameof(WildcardNotificationsAction));
    }
    partial void OnPersonalColorChanged(string value)
    {
        var selectedColor = TryNormalizePersonalColor(value, out var normalized) ? normalized : null;
        foreach (var option in ColorPalette) option.IsSelected = option.Hex == selectedColor;
        OnPropertyChanged(nameof(HasValidPersonalColor));
        OnPropertyChanged(nameof(PersonalColorPreview));
        OnPropertyChanged(nameof(PersonalColorPreviewSoft));
        OnPropertyChanged(nameof(PersonalColorPreviewLabel));
        OnPropertyChanged(nameof(PersonalColorError));
        OnPropertyChanged(nameof(HasPersonalColorError));
        OnPropertyChanged(nameof(CanSavePersonalColor));
    }
    partial void OnIsMemberDataCurrentChanged(bool value)
    {
        NotifyAccess();
        OnPropertyChanged(nameof(MemberManagementStatus));
        OnPropertyChanged(nameof(HasMemberManagementStatus));
    }
    partial void OnPendingMemberRemovalChanged(ChannelMemberItem? value)
    {
        OnPropertyChanged(nameof(MemberRemovalConfirmationText));
        OnPropertyChanged(nameof(CanConfirmRemoveMember));
    }
    partial void OnIsMemberRemovalConfirmationOpenChanged(bool value) => OnPropertyChanged(nameof(CanConfirmRemoveMember));
    partial void OnIsColorPickerOpenChanged(bool value) { }
    partial void OnSelectedPermissionChanged(ChannelPermissionItem? value)
    {
        SelectedNamedGroup = (value?.Value as NamedChannelGroupSetting) is { } named
            ? NamedGroups.FirstOrDefault(group => group.GroupId == named.GroupId)
            : null;
        OnPropertyChanged(nameof(CanSaveSelectedNamedGroup));
    }
    partial void OnActiveTabChanged(ChannelSettingsTab value)
    {
        OnPropertyChanged(nameof(IsGeneralTab)); OnPropertyChanged(nameof(IsPersonalTab)); OnPropertyChanged(nameof(IsSubscribersTab)); OnPropertyChanged(nameof(IsPermissionsTab));
    }
    partial void OnMemberSearchTextChanged(string value) => NotifyMemberProjection();
    partial void OnNewChannelIsPrivateChanged(bool value)
    {
        var privacyIndex = value ? 1 : 0;
        if (NewChannelPrivacyIndex != privacyIndex) NewChannelPrivacyIndex = privacyIndex;
        if (value)
        {
            NewChannelIsDefault = false;
        }
        else NewChannelHistoryPublic = true;
        OnPropertyChanged(nameof(CanShareNewChannelHistory));
        OnPropertyChanged(nameof(CanSetNewChannelDefault));
        OnPropertyChanged(nameof(NewChannelPrivacyDescription));
    }
    partial void OnNewChannelPrivacyIndexChanged(int value)
    {
        var isPrivate = value == 1;
        if (NewChannelIsPrivate != isPrivate) NewChannelIsPrivate = isPrivate;
    }
    partial void OnNewChannelNameChanged(string value) => OnPropertyChanged(nameof(CanSubmitCreateChannel));
    partial void OnIsCreateChannelOpenChanged(bool value) => OnPropertyChanged(nameof(CanSubmitCreateChannel));
    partial void OnDraftIsPrivateChanged(bool value)
    {
        if (value) DraftIsDefault = false;
        else DraftHistoryPublic = true;
        OnPropertyChanged(nameof(CanSetDraftDefault));
    }
    partial void OnDraftIsWebPublicChanged(bool value)
    {
        if (value)
        {
            DraftIsPrivate = false;
            DraftHistoryPublic = true;
        }
    }
    partial void OnSelectedNamedGroupChanged(ChannelUserGroup? value) => OnPropertyChanged(nameof(CanSaveSelectedNamedGroup));
    partial void OnNewFolderNameChanged(string value) => OnPropertyChanged(nameof(CanCreateNewFolder));
    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDesktopListVisible));
        OnPropertyChanged(nameof(IsDesktopDetailVisible));
        OnPropertyChanged(nameof(IsNarrowListVisible));
        OnPropertyChanged(nameof(IsNarrowDetailVisible));
        OnPropertyChanged(nameof(IsNarrowBackVisible));
        OnPropertyChanged(nameof(ListPaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
    }
    partial void OnIsCompactSettingsHeaderChanged(bool value) { }
    partial void OnIsListVisibleOnNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNarrowListVisible));
        OnPropertyChanged(nameof(IsNarrowDetailVisible));
        OnPropertyChanged(nameof(IsNarrowBackVisible));
        OnPropertyChanged(nameof(ListPaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
    }
    partial void OnConfirmationChanged(ChannelSettingsConfirmation value)
    {
        OnPropertyChanged(nameof(IsConfirmationOpen));
        OnPropertyChanged(nameof(ConfirmationText));
    }

    private bool IsCurrent(long generation, CancellationTokenSource cancellation) => !_disposed && IsOpen && generation == _generation && ReferenceEquals(cancellation, _loadCancellation) && !cancellation.IsCancellationRequested;
    private bool IsCurrentTab(long generation, CancellationTokenSource rootCancellation, ChannelSettingsTab requestedTab, long tabGeneration, CancellationTokenSource tabCancellation, long targetId) =>
        IsCurrent(generation, rootCancellation) && ActiveTab == requestedTab && tabGeneration == _tabGeneration &&
        ReferenceEquals(tabCancellation, _tabCancellation) && !tabCancellation.IsCancellationRequested && SelectedChannel?.ChannelId == targetId;
    private void CancelLoad()
    {
        CancelTabLoad();
        CancelEmail();
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void CancelEmail()
    {
        var cancellation = Interlocked.Exchange(ref _emailCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        EmailAddress = null;
    }

    private void CancelTabLoad()
    {
        var cancellation = Interlocked.Exchange(ref _tabCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsTabLoading = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelLoad();
        Members.CollectionChanged -= OnMembersCollectionChanged;
        foreach (var member in Members) member.PropertyChanged -= OnMemberPropertyChanged;
    }
}
