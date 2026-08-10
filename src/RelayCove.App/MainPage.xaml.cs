using RelayCove.App.ViewModels;

namespace RelayCove.App;

public partial class MainPage : ContentPage
{
    private readonly ShellViewModel _viewModel;

    public MainPage(ShellViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}
