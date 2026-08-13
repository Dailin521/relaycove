namespace RelayCove.App.Services;

public interface IAppearanceService
{
    AppAppearanceMode Current { get; }
    void Apply(AppAppearanceMode mode);
}
