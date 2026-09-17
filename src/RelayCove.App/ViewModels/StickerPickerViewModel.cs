using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class StickerPickerViewModel : ObservableObject, IDisposable
{
    private readonly IClientSession _session;
    private readonly IStickerCatalogService _catalog;
    private readonly IStickerLibraryStore _library;
    private readonly IFileSelectionService _files;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _operation;
    private AccountId? _account;
    private ConversationKey? _sendConversation;
    private bool _sendStarted;
    private bool _open;
    private bool _disposed;
    private bool _catalogOffline;
    private IReadOnlyList<StickerCatalogEntry> _entries = [];
    private IReadOnlyList<StickerFavorite> _favorites = [];
    private IReadOnlyList<StickerPickerItem> _matches = [];

    public StickerPickerViewModel(IClientSession session, IStickerCatalogService catalog,
        IStickerLibraryStore library, IFileSelectionService files, IUiDispatcher dispatcher)
    {
        _session = session;
        _catalog = catalog;
        _library = library;
        _files = files;
        _dispatcher = dispatcher;
        _account = session.AccountId;
        session.StateChanged += OnSessionChanged;
    }

    public event EventHandler? Sent;
    public ObservableCollection<StickerPickerItem> Items { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDefault), nameof(IsSearch), nameof(IsFavorites), nameof(IsImageTab), nameof(HasMoreItems))]
    public partial string Tab { get; set; } = "default";
    public bool IsDefault => Tab == "default";
    public bool IsSearch => Tab == "search";
    public bool IsFavorites => Tab == "favorites";
    public bool IsImageTab => !IsDefault;
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct))] public partial bool IsWorking { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEditing))] public partial StickerPickerItem? EditingItem { get; set; }
    [ObservableProperty] public partial string EditLabel { get; set; } = "";
    public bool IsEditing => EditingItem is not null;
    public bool CanAct => !IsWorking && _session.AccountId is not null;
    public bool HasStatus => Status.Length > 0;
    public bool HasMoreItems => Items.Count < _matches.Count;

    public void SetOpen(bool open)
    {
        if (open && !_open)
        {
            Tab = "default";
            Query = "";
        }
        _open = open;
        if (open) _ = RefreshAsync(reloadSource: true);
        else
        {
            _refresh?.Cancel();
            IsLoading = false;
            EditingItem = null;
            Items.Clear();
            _matches = [];
            OnPropertyChanged(nameof(HasMoreItems));
        }
    }

    [RelayCommand]
    private void SelectTab(string tab)
    {
        if (tab is not ("default" or "search" or "favorites") || Tab == tab) return;
        Tab = tab;
        EditingItem = null;
        Query = "";
        _ = RefreshAsync(reloadSource: true);
    }

    partial void OnQueryChanged(string value) { if (_open) _ = RefreshAsync(debounce: true); }
    [RelayCommand]
    private Task ReloadAsync() => RefreshAsync(reloadSource: true);

    private async Task RefreshAsync(bool debounce = false, bool reloadSource = false)
    {
        _refresh?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refresh = cancellation;
        var token = cancellation.Token;
        var account = _session.AccountId;
        var tab = Tab;
        IsLoading = false;
        Items.Clear();
        _matches = [];
        OnPropertyChanged(nameof(HasMoreItems));
        if (!_open || IsDefault || account is null) { _refresh = null; return; }
        try
        {
            if (debounce) await Task.Delay(300, token);
            IsLoading = true;
            if (tab == "search")
            {
                if (reloadSource || _entries.Count == 0)
                {
                    var snapshot = await _catalog.GetCatalogAsync(token);
                    token.ThrowIfCancellationRequested();
                    _entries = snapshot.Entries;
                    _catalogOffline = snapshot.IsOffline;
                }
                Status = _catalogOffline ? "当前离线，显示缓存目录；已缓存的表情仍可使用。" : "";
                if (_catalogOffline && _entries.Count == 0)
                {
                    Status = "无法获取表情目录，请检查网络后点击重试。";
                    return;
                }
                var words = (Query ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _matches = _entries.Where(entry =>
                    words.All(word => entry.Label.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new StickerPickerItem(entry, null)).ToArray();
            }
            else
            {
                _favorites = await _library.ListAsync(account.Value.Value, token);
                token.ThrowIfCancellationRequested();
                if (_session.AccountId != account) return;
                Status = "";
                _matches = _favorites.Where(item => item.Label.Contains((Query ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(item => new StickerPickerItem(null, item, account.Value.Value)).ToArray();
            }
            LoadMore();
            if (Items.Count == 0) Status = tab == "favorites" ? "暂无匹配的收藏，可导入图片或收藏聊天图片。" : "没有匹配的表情，请换个名称试试。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (!token.IsCancellationRequested) Status = "表情加载失败，请检查网络后点击重试。"; }
        finally
        {
            if (ReferenceEquals(_refresh, cancellation)) { _refresh = null; IsLoading = false; }
        }
    }

    [RelayCommand]
    private void LoadMore()
    {
        foreach (var item in _matches.Skip(Items.Count).Take(60)) Items.Add(item);
        OnPropertyChanged(nameof(HasMoreItems));
    }

    public async Task<StickerMedia> LoadImageAsync(StickerPickerItem item, bool thumbnail, CancellationToken token)
    {
        if (item.Catalog is { } entry) return await _catalog.GetMediaAsync(entry, thumbnail, token);
        if (item.Favorite is not { } favorite || item.AccountKey is not { } account || _session.AccountId?.Value != account)
            throw new OperationCanceledException();
        var result = await _library.ReadAsync(account, favorite.Hash, token);
        if (_session.AccountId?.Value != account) throw new OperationCanceledException();
        return result;
    }

    [RelayCommand]
    private async Task SendAsync(StickerPickerItem? item)
    {
        if (item is null || !CanAct || _session.SelectedConversation is not { } conversation ||
            _session.State.Connection.Status != ConnectionStatus.Connected) return;
        _sendConversation = conversation;
        _sendStarted = false;
        await RunOperationAsync(async (account, token) =>
        {
            Status = "正在准备表情…";
            var media = await LoadImageAsync(item, thumbnail: false, token);
            EnsureSendTarget(account, conversation, token);
            if (media.Content.LongLength > Math.Min(StickerImageContent.MaximumBytes, _session.MaxFileUploadBytes))
            {
                Status = "表情超过当前服务器允许的上传大小。";
                return;
            }
            using var stream = new MemoryStream(media.Content, writable: false);
            Status = "正在上传表情…";
            var upload = await _session.UploadAttachmentAsync(new AttachmentUpload(media.FileName, media.ContentType, media.Content.Length, stream), token);
            EnsureSendTarget(account, conversation, token);
            Status = "正在发送表情…";
            _sendStarted = true;
            await _session.SendAsync(conversation, ShellViewModel.BuildUploadedAttachmentMarkdown(upload, isImage: true), token);
            if (_session.AccountId == account && _session.SelectedConversation == conversation)
            {
                Status = "";
                Sent?.Invoke(this, EventArgs.Empty);
            }
        }, "表情发送未确认，请查看消息状态后再决定是否重试。", cancelOnNavigation: true);
        _sendConversation = null;
        _sendStarted = false;
    }

    private void EnsureSendTarget(AccountId account, ConversationKey conversation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_session.AccountId != account || _session.SelectedConversation != conversation) throw new OperationCanceledException();
    }

    [RelayCommand]
    private async Task ManageAsync(StickerPickerItem? item)
    {
        if (item is null || !CanAct) return;
        if (item.Favorite is not null)
        {
            if (item.AccountKey != _session.AccountId?.Value) return;
            EditingItem = item;
            EditLabel = item.Label;
            return;
        }
        await RunOperationAsync(async (account, token) =>
        {
            var media = await LoadImageAsync(item, thumbnail: false, token);
            using var stream = new MemoryStream(media.Content, writable: false);
            await _library.AddAsync(account.Value, stream, item.Label, token);
            Status = "已加入收藏。";
        }, "收藏失败，请检查图片或网络后重试。");
    }

    [RelayCommand]
    private Task ImportAsync() => RunOperationAsync(async (account, token) =>
    {
        var files = await _files.PickStickersAsync(token);
        var count = 0;
        var failed = 0;
        foreach (var file in files.Take(50))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (file.Length > StickerImageContent.MaximumBytes) { failed++; continue; }
                await using var stream = await file.OpenReadAsync(token);
                await _library.AddAsync(account.Value, stream, Path.GetFileNameWithoutExtension(file.FileName), token);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        if (_session.AccountId != account) return;
        await RefreshAsync();
        Status = files.Count == 0 ? Status : $"已导入 {count} 张，跳过 {failed + Math.Max(0, files.Count - 50)} 张（支持 PNG/JPEG/WebP/GIF，单张上限 25 MiB）。";
    }, "导入失败，请检查图片后重试。");

    public Task CollectMessageImageAsync(MessageAttachmentItem attachment) => RunOperationAsync(async (account, token) =>
    {
        // Protected Realm media always uses the authenticated session, never the public catalog client.
        var media = await _session.GetRealmMediaAsync(new RealmMediaRequest(attachment.SourceUrl, RealmMediaKind.Image, StickerImageContent.MaximumBytes), token);
        token.ThrowIfCancellationRequested();
        if (_session.AccountId != account) return;
        using var stream = new MemoryStream(media.Content, writable: false);
        await _library.AddAsync(account.Value, stream, Path.GetFileNameWithoutExtension(attachment.Name), token);
        Status = "已加入表情收藏。";
    }, "收藏失败，图片可能不可用或超过 25 MiB。");

    [RelayCommand]
    private Task SaveNameAsync() => RunOperationAsync(async (account, token) =>
    {
        if (EditingItem?.Favorite is not { } favorite || EditingItem.AccountKey != account.Value || string.IsNullOrWhiteSpace(EditLabel)) return;
        await _library.RenameAsync(account.Value, favorite.Hash, EditLabel.Trim(), token);
        EditingItem = null;
        await RefreshAsync();
    }, "重命名失败，请重试。");

    [RelayCommand]
    private Task RemoveAsync() => RunOperationAsync(async (account, token) =>
    {
        if (EditingItem?.Favorite is not { } favorite || EditingItem.AccountKey != account.Value) return;
        await _library.RemoveAsync(account.Value, favorite.Hash, token);
        EditingItem = null;
        await RefreshAsync();
    }, "移除失败，请重试。");

    [RelayCommand] private void CancelEdit() => EditingItem = null;

    private async Task RunOperationAsync(Func<AccountId, CancellationToken, Task> action, string error, bool cancelOnNavigation = false)
    {
        if (!CanAct || _disposed || _session.AccountId is not { } account) return;
        IsWorking = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation;
        if (!cancelOnNavigation) _sendConversation = null;
        try { await action(account, cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (_session.AccountId == account && !_disposed) Status = _sendStarted ? error : "操作已取消。";
        }
        catch { if (_session.AccountId == account && !_disposed) Status = error; }
        finally
        {
            _operation = null;
            IsWorking = false;
        }
    }

    private void OnSessionChanged(object? sender, ClientStateChangedEventArgs args)
    {
        var accountChanged = _account != _session.AccountId;
        // Cancel immediately, before a queued UI projection can let an old operation continue.
        if (accountChanged || !_sendStarted && _sendConversation is { } target && target != _session.SelectedConversation)
            _operation?.Cancel();
        if (!accountChanged) return;
        _account = _session.AccountId;
        _refresh?.Cancel();
        _dispatcher.Dispatch(() =>
        {
            if (_disposed) return;
            _favorites = [];
            Items.Clear();
            _matches = [];
            EditingItem = null;
            Status = "";
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(HasMoreItems));
            if (_open) _ = RefreshAsync();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StateChanged -= OnSessionChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
