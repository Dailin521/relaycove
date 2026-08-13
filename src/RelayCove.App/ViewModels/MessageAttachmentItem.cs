namespace RelayCove.App.ViewModels;

public sealed record MessageAttachmentItem(string Kind, string Name, string SourceUrl)
{
    public bool IsImage => string.Equals(Kind, "image", StringComparison.Ordinal);
    public bool IsFile => !IsImage;
    public string KindLabel => IsImage ? "图片" : "文件";
    public string AccessibleLabel => $"{KindLabel}附件 {Name}";
}
