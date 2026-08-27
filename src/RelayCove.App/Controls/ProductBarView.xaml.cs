using System.ComponentModel;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ProductBarView : TitleBar
{
    private readonly IWindowShellAdapter _windowShellAdapter;
    private ShellViewModel? _viewModel;

    public static readonly BindableProperty IsAccountMenuOpenProperty = BindableProperty.Create(
        nameof(IsAccountMenuOpen),
        typeof(bool),
        typeof(ProductBarView));

    public ProductBarView(IWindowShellAdapter windowShellAdapter)
    {
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
        InitializeComponent();
    }

    public bool IsAccountMenuOpen
    {
        get => (bool)GetValue(IsAccountMenuOpenProperty);
        set => SetValue(IsAccountMenuOpenProperty, value);
    }

    public void Bind(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        BindingContext = viewModel;
        AccountButton.Command = viewModel.ToggleAccountMenuCommand;
        DownloadButton.Command = viewModel.ToggleDownloadCenterCommand;
        SettingsButton.Command = viewModel.ToggleSettingsCommand;
        ThemeButton.Command = viewModel.ToggleThemeCommand;
        AccountButtonBorder.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.MainVisible), source: viewModel));
        AccountInitialLabel.SetBinding(
            Label.TextProperty,
            new Binding(nameof(ShellViewModel.CurrentUserInitial), source: viewModel));
        AccountAvatar.SetBinding(
            RealmMediaImageView.SourceUrlProperty,
            new Binding(nameof(ShellViewModel.CurrentUserAvatarUrl), source: viewModel));
        ProductBarOwnPresenceDot.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.HasOwnPresenceStatus), source: viewModel));
        ProductBarOwnPresenceDot.SetBinding(
            Border.BackgroundProperty,
            new Binding(nameof(ShellViewModel.OwnPresenceBrush), source: viewModel));
        SettingsButton.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.MainVisible), source: viewModel));
        SetBinding(
            IsAccountMenuOpenProperty,
            new Binding(nameof(ShellViewModel.IsAccountMenuOpen), source: viewModel));
        ConnectionStatusLabel.SetBinding(
            Label.TextProperty,
            new Binding(nameof(ShellViewModel.ConnectionStatus), source: viewModel));
        ConnectionStatusBorder.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.ShowConnectionStatus), source: viewModel));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SynchronizeDownloadAttention();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is not nameof(ShellViewModel.HasUnseenCompletedDownloads) and
            not nameof(ShellViewModel.HasUnseenDownloadFailure))
        {
            return;
        }

        Dispatcher.Dispatch(SynchronizeDownloadAttention);
    }

    private void SynchronizeDownloadAttention()
    {
        CompletedDownloadDot.IsVisible = _viewModel?.HasUnseenCompletedDownloads == true;
        FailedDownloadDot.IsVisible = _viewModel?.HasUnseenDownloadFailure == true;
    }

    private void OnPinClicked(object? sender, EventArgs eventArgs)
    {
        _windowShellAdapter.TogglePinned();
    }
}
