using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentImageViewState : INotifyPropertyChanged
{
    private const string UnavailableStatusText = "图片预览不可用。";
    private const string PendingStatusText = "图片缩略图待加载。";
    private const string LoadingStatusText = "正在加载图片缩略图…";
    private const string LoadedStatusText = "图片缩略图已加载。";

    private bool isEligible;
    private BitmapSource? thumbnail;
    private bool isLoading;
    private string statusText;

    public ClientAttachmentImageViewState(
        ClientAttachmentDownloadContext context,
        string displayName,
        bool eligible)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A display name is required.", nameof(displayName));
        }

        Context = context;
        DisplayName = displayName;
        isEligible = eligible;
        statusText = eligible ? PendingStatusText : UnavailableStatusText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClientAttachmentDownloadContext Context { get; }

    public string DisplayName { get; }

    public bool IsEligible
    {
        get => isEligible;
        private set
        {
            if (!SetField(ref isEligible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowPreview));
            OnPropertyChanged(nameof(CanView));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        private set
        {
            if (!SetField(ref thumbnail, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanView));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (!SetField(ref isLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanView));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public bool ShowPreview => IsEligible;

    public bool CanView => IsEligible && Thumbnail is not null && !IsLoading;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string AutomationName =>
        CanView
            ? $"查看图片：{DisplayName}"
            : IsLoading
                ? $"正在加载图片预览：{DisplayName}"
                : IsEligible
                    ? $"图片预览待加载：{DisplayName}"
                    : $"图片预览不可用：{DisplayName}";

    public void SynchronizeEligibility(bool eligible)
    {
        if (IsEligible == eligible)
        {
            return;
        }

        IsEligible = eligible;
        Thumbnail = null;
        IsLoading = false;
        StatusText = eligible ? PendingStatusText : UnavailableStatusText;
    }

    public bool TryBeginLoad()
    {
        if (!IsEligible || IsLoading || Thumbnail is not null)
        {
            return false;
        }

        IsLoading = true;
        StatusText = LoadingStatusText;
        return true;
    }

    public bool TryApplyLoaded(BitmapSource loadedThumbnail)
    {
        ArgumentNullException.ThrowIfNull(loadedThumbnail);

        if (!IsEligible || !IsLoading || !loadedThumbnail.IsFrozen)
        {
            return false;
        }

        Thumbnail = loadedThumbnail;
        IsLoading = false;
        StatusText = LoadedStatusText;
        return true;
    }

    public bool TryApplyFailure(string safeStatus)
    {
        if (string.IsNullOrWhiteSpace(safeStatus))
        {
            throw new ArgumentException("A safe status is required.", nameof(safeStatus));
        }

        if (!IsEligible || !IsLoading)
        {
            return false;
        }

        IsLoading = false;
        StatusText = safeStatus;
        return true;
    }

    public void ClearForRecycle()
    {
        Thumbnail = null;
        IsLoading = false;
        StatusText = IsEligible ? PendingStatusText : UnavailableStatusText;
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentImageViewState)} {{ Context = [REDACTED], " +
        "DisplayName = [REDACTED], IsEligible = [REDACTED], " +
        $"HasThumbnail = {Thumbnail is not null}, IsLoading = {IsLoading} }}";

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
