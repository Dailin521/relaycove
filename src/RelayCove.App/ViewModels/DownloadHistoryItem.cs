using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.App.Services;

namespace RelayCove.App.ViewModels;

public sealed partial class DownloadHistoryItem : ObservableObject
{
    public DownloadHistoryItem(DownloadHistoryEntry entry, bool isMissing = false)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        IsMissing = isMissing;
    }

    public DownloadHistoryEntry Entry { get; }
    public Guid Id => Entry.Id;
    public string FileName => Entry.FileName;
    public string FilePath => Entry.FilePath;
    public long Length => Entry.Length;
    public string LengthLabel => FormatBytes(Length);
    public string CompletedAtLabel => Entry.CompletedAt.LocalDateTime.ToString("MM/dd HH:mm");
    public string StatusLabel => IsMissing ? "文件已移动或删除" : $"{LengthLabel} · {CompletedAtLabel}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial bool IsMissing { get; set; }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;
        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}
