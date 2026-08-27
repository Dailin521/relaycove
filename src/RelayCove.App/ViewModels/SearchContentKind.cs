namespace RelayCove.App.ViewModels;

[Flags]
public enum SearchContentKind
{
    Message = 1,
    File = 2,
    Image = 4,
    Video = 8,
    Link = 16
}
