using System.Collections.ObjectModel;
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
    private long _generation;
    private ChannelSettingsSnapshot? _snapshot;
    private ChannelDetails? _details;
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
    }

    public ObservableCollection<ChannelSettingsChannelItem> Channels { get; } = [];
    public ObservableCollection<ChannelFolder> Folders { get; } = [];
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
    public bool CanArchive => _isAuthorityCurrent && _snapshot?.IsOrganizationAdministrator == true && _details is { IsArchived: false } && !IsBusy;
    public bool CanUnsubscribe => _isAuthorityCurrent && SelectedChannel?.IsSubscribed == true && !IsBusy;
    public bool CanSubscribe => _isAuthorityCurrent && Access.CanSubscribe && !IsBusy;
    public bool CanChangeSubscription => CanUnsubscribe || CanSubscribe;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasEmailAddress => !string.IsNullOrWhiteSpace(EmailAddress);
    public bool IsDesktopListVisible => !IsNarrow;
    public bool IsDesktopDetailVisible => !IsNarrow;
    public bool IsNarrowListVisible => IsNarrow && IsListVisibleOnNarrow;
    public bool IsNarrowDetailVisible => IsNarrow && !IsListVisibleOnNarrow;
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
    public string SelectedName => SelectedChannel?.Name ?? "频道";
    public string PrivacyLabel => _details?.IsPrivate == true ? "私有频道" : "公开频道";
    public string DescriptionLabel => string.IsNullOrWhiteSpace(_details?.Description) ? "暂无频道说明" : _details!.Description!;
    public string CreatorLabel => _details?.CreatorId is { } id
        ? _session.State.Users.TryGetValue(id, out var user) ? $"由 {user.FullName} 创建" : $"创建者 ID: {id}"
        : "创建者信息不可用";
    public string DateLabel => _details?.DateCreated is { } date ? $"创建于 {date.LocalDateTime:yyyy年M月d日}" : "创建日期不可用";
    public string ChannelIdLabel => _details is { } details ? $"频道 ID: {details.ChannelId}" : string.Empty;
    public string FolderLabel => DraftFolderId is null ? "没有" : Folders.FirstOrDefault(folder => folder.FolderId == DraftFolderId)?.Name ?? "没有";
    public string SubscriptionActionLabel => SelectedChannel?.IsSubscribed == true ? "退出" : "订阅";
    public GridLength ListPaneWidth => !IsNarrow ? new GridLength(400d) : IsListVisibleOnNarrow ? GridLength.Star : new GridLength(0d);
    public GridLength DetailPaneWidth => !IsNarrow ? GridLength.Star : IsListVisibleOnNarrow ? new GridLength(0d) : GridLength.Star;
    public string ConfirmationText => Confirmation switch
    {
        ChannelSettingsConfirmation.Unsubscribe => $"确定退出频道“{SelectedName}”吗？",
        ChannelSettingsConfirmation.Archive => $"确定归档频道“{SelectedName}”吗？归档后频道将不再活跃。",
        _ => string.Empty
    };

    public async Task OpenAsync(long channelId)
    {
        if (channelId <= 0) return;
        IsOpen = true;
        IsListVisibleOnNarrow = !IsNarrow;
        await ReloadAsync(channelId);
    }

    [RelayCommand]
    public void Close()
    {
        CancelLoad();
        IsOpen = false;
        IsEditDialogOpen = false;
        IsCreateFolderOpen = false;
        Confirmation = ChannelSettingsConfirmation.None;
        _isAuthorityCurrent = false;
        EmailAddress = null;
        Error = null;
    }

    [RelayCommand]
    private void CloseTopLayer()
    {
        if (Confirmation != ChannelSettingsConfirmation.None) CancelConfirmation();
        else if (IsCreateFolderOpen) CancelCreateFolder();
        else if (IsEditDialogOpen) CancelEdit();
        else Close();
    }

    public void UpdateViewport(double width)
    {
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

    [RelayCommand(AllowConcurrentExecutions = false)]
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
    private void RequestArchive() { if (CanArchive) Confirmation = ChannelSettingsConfirmation.Archive; }

    [RelayCommand]
    private void CancelConfirmation() => Confirmation = ChannelSettingsConfirmation.None;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmAsync()
    {
        if (SelectedChannel is null || Confirmation == ChannelSettingsConfirmation.None) return;
        var targetId = SelectedChannel.ChannelId;
        var action = Confirmation;
        await ExecuteWriteAsync(async token =>
        {
            if (action == ChannelSettingsConfirmation.Unsubscribe) await _session.UnsubscribeChannelAsync(targetId, token);
            else await _session.ArchiveChannelAsync(targetId, token);
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

    private async Task ReloadAsync(long? desiredChannelId)
    {
        CancelLoad();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var generation = ++_generation;
        IsLoading = true;
        _isAuthorityCurrent = false;
        NotifyAccess();
        Error = null;
        try
        {
            _snapshot = await _session.LoadChannelSettingsSnapshotAsync(cancellation.Token);
            if (!IsCurrent(generation, cancellation)) return;
            ReconcileChannels(_snapshot.Channels);
            ReconcileFolders(_snapshot.Folders);
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
            DraftFolderId = details.FolderId;
            IsFolderDirty = false;
            _isAuthorityCurrent = true;
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
        OnPropertyChanged(nameof(CanUnsubscribe));
        OnPropertyChanged(nameof(CanSubscribe));
        OnPropertyChanged(nameof(CanChangeSubscription));
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        OnPropertyChanged(nameof(CanSaveFolder));
        OnPropertyChanged(nameof(CanCreateNewFolder));
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredChannels));
    partial void OnListModeChanged(ChannelSettingsListMode value) => OnPropertyChanged(nameof(FilteredChannels));
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
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        NotifyAccess();
    }
    partial void OnDraftFolderIdChanged(long? value)
    {
        if (DraftFolder?.FolderId != value)
            DraftFolder = Folders.FirstOrDefault(folder => folder.FolderId == value);
        IsFolderDirty = _details?.FolderId != value;
        OnPropertyChanged(nameof(FolderLabel));
    }
    partial void OnDraftFolderChanged(ChannelFolder? value)
    {
        if (DraftFolderId != value?.FolderId) DraftFolderId = value?.FolderId;
    }
    partial void OnIsFolderDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSaveFolder));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanSaveEdit)); NotifyAccess(); }
    partial void OnEditValueChanged(string value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnEditKindChanged(ChannelSettingsEditKind value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnIsEditDialogOpenChanged(bool value) => OnPropertyChanged(nameof(CanSaveEdit));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnEmailAddressChanged(string? value) => OnPropertyChanged(nameof(HasEmailAddress));
    partial void OnNewFolderNameChanged(string value) => OnPropertyChanged(nameof(CanCreateNewFolder));
    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDesktopListVisible));
        OnPropertyChanged(nameof(IsDesktopDetailVisible));
        OnPropertyChanged(nameof(IsNarrowListVisible));
        OnPropertyChanged(nameof(IsNarrowDetailVisible));
        OnPropertyChanged(nameof(ListPaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
    }
    partial void OnIsListVisibleOnNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNarrowListVisible));
        OnPropertyChanged(nameof(IsNarrowDetailVisible));
        OnPropertyChanged(nameof(ListPaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
    }
    partial void OnConfirmationChanged(ChannelSettingsConfirmation value)
    {
        OnPropertyChanged(nameof(IsConfirmationOpen));
        OnPropertyChanged(nameof(ConfirmationText));
    }

    private bool IsCurrent(long generation, CancellationTokenSource cancellation) => !_disposed && IsOpen && generation == _generation && ReferenceEquals(cancellation, _loadCancellation) && !cancellation.IsCancellationRequested;
    private void CancelLoad()
    {
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelLoad();
    }
}
