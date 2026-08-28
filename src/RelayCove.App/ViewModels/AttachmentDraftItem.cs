using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class AttachmentDraftItem : ObservableObject
{
    private static readonly HashSet<string> PreviewImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/avif", "image/gif", "image/jpeg", "image/png", "image/webp"
    };

    public AttachmentDraftItem(SelectedAttachmentFile file)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public SelectedAttachmentFile File { get; }
    public string FileName => File.FileName;
    public long Length => File.Length;
    public string SizeLabel => FormatBytes(Length);
    public bool IsImage => File.ContentType is { } contentType && PreviewImageTypes.Contains(contentType);
    public ImageSource? PreviewSource => File.HasPreview
        ? field ??= ImageSource.FromStream(File.OpenPreviewStream)
        : null;
    public bool HasPreview => File.HasPreview;
    public bool HasGenericIcon => !HasPreview;
    public string KindLabel => IsImage ? "图片" : "文件";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial AttachmentUploadStatus Status { get; set; }

    [ObservableProperty]
    public partial UploadedAttachment? Uploaded { get; set; }

    [ObservableProperty]
    public partial double UploadProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial string? UploadProgressLabel { get; set; }

    public string StatusLabel => Status switch
    {
        AttachmentUploadStatus.Pending => string.Empty,
        AttachmentUploadStatus.Uploading => $"正在上传 {UploadProgressLabel ?? "0%"}",
        AttachmentUploadStatus.Uploaded => "已上传",
        AttachmentUploadStatus.Uncertain => "上传结果未知；不会自动重试",
        AttachmentUploadStatus.Failed => "上传失败；可显式重试",
        _ => string.Empty
    };
    public bool CanRetry => Status is AttachmentUploadStatus.Uncertain or AttachmentUploadStatus.Failed;
    public bool CanRemove => Status != AttachmentUploadStatus.Uploading;
    public bool IsUploading => Status == AttachmentUploadStatus.Uploading;

    partial void OnStatusChanged(AttachmentUploadStatus value) =>
        OnPropertyChanged(nameof(IsUploading));

    public void BeginUpload()
    {
        UploadProgress = 0;
        UploadProgressLabel = "0%";
        Status = AttachmentUploadStatus.Uploading;
    }

    public void ReportUploadProgress(RealmMediaTransferProgress progress)
    {
        var total = progress.TotalBytes is > 0 ? progress.TotalBytes.Value : Length;
        UploadProgress = Math.Clamp(progress.BytesTransferred / (double)total, 0d, 1d);
        UploadProgressLabel = $"{UploadProgress:P0}";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KB";
        var value = bytes / (1024d * 1024d);
        return value == Math.Truncate(value) ? $"{value:F0} MB" : $"{value:F1} MB";
    }
}
