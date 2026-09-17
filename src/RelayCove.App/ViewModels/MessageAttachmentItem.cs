using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace RelayCove.App.ViewModels;

public sealed record MessageAttachmentItem(string Kind, string Name, string SourceUrl) : INotifyPropertyChanged
{
    private bool _isDownloaded;
    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public bool IsDownloadIndeterminate { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DownloadActionText => IsDownloading ? "下载中" : _isDownloaded ? "打开" : "下载";
    internal void SetTransfer(bool active, double progress, bool indeterminate)
    {
        IsDownloading = active;
        DownloadProgress = progress;
        IsDownloadIndeterminate = indeterminate;
        foreach (var name in new[] { nameof(IsDownloading), nameof(DownloadProgress), nameof(IsDownloadIndeterminate), nameof(DownloadActionText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    internal string AttachmentKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceUrl)));
    internal void SetDownloaded(bool exists)
    {
        if (_isDownloaded == exists) return;
        _isDownloaded = exists;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadActionText)));
    }

    // Local download availability does not change attachment identity.
    public bool Equals(MessageAttachmentItem? other) =>
        other is not null && Kind == other.Kind && Name == other.Name && SourceUrl == other.SourceUrl;
    public override int GetHashCode() => HashCode.Combine(Kind, Name, SourceUrl);
    public bool IsImage => string.Equals(Kind, "image", StringComparison.Ordinal);
    public bool IsFile => !IsImage;
    public string? ImageSourceUrl => IsImage ? SourceUrl : null;
    public string KindLabel => IsImage ? "图片" : "文件";
    public string AccessibleLabel => $"{KindLabel}附件 {Name}";
}
