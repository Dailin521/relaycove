using System.ComponentModel;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ProductBarView : TitleBar
{
    private readonly IWindowShellAdapter _windowShellAdapter;
    private ShellViewModel? _viewModel;

    public static readonly BindableProperty IsPinnedProperty = BindableProperty.Create(
        nameof(IsPinned),
        typeof(bool),
        typeof(ProductBarView));

    public static readonly BindableProperty IsAccountMenuOpenProperty = BindableProperty.Create(
        nameof(IsAccountMenuOpen),
        typeof(bool),
        typeof(ProductBarView));

    public static readonly BindableProperty IsSettingsSectionProperty = BindableProperty.Create(
        nameof(IsSettingsSection),
        typeof(bool),
        typeof(ProductBarView));

    public static readonly BindableProperty IsDownloadCenterOpenProperty = BindableProperty.Create(
        nameof(IsDownloadCenterOpen),
        typeof(bool),
        typeof(ProductBarView));

    public ProductBarView(IWindowShellAdapter windowShellAdapter)
    {
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
        InitializeComponent();
        _windowShellAdapter.StateChanged += OnWindowStateChanged;
        IsPinned = _windowShellAdapter.IsPinned;
    }

    public bool IsPinned
    {
        get => (bool)GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public bool IsAccountMenuOpen
    {
        get => (bool)GetValue(IsAccountMenuOpenProperty);
        set => SetValue(IsAccountMenuOpenProperty, value);
    }

    public bool IsSettingsSection
    {
        get => (bool)GetValue(IsSettingsSectionProperty);
        set => SetValue(IsSettingsSectionProperty, value);
    }

    public bool IsDownloadCenterOpen
    {
        get => (bool)GetValue(IsDownloadCenterOpenProperty);
        set => SetValue(IsDownloadCenterOpenProperty, value);
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
        SetBinding(
            IsSettingsSectionProperty,
            new Binding(nameof(ShellViewModel.IsSettingsSection), source: viewModel));
        SetBinding(
            IsDownloadCenterOpenProperty,
            new Binding(nameof(ShellViewModel.IsDownloadCenterOpen), source: viewModel));
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
        IsPinned = _windowShellAdapter.IsPinned;
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.Dispatch(() => IsPinned = _windowShellAdapter.IsPinned);
}
