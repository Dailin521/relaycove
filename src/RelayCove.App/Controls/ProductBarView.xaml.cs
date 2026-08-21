using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ProductBarView : TitleBar
{
    private readonly IWindowShellAdapter _windowShellAdapter;

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

    public void Bind(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        BindingContext = viewModel;
        AccountButton.Command = viewModel.ToggleAccountMenuCommand;
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
        SettingsButton.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.MainVisible), source: viewModel));
        SetBinding(
            IsAccountMenuOpenProperty,
            new Binding(nameof(ShellViewModel.IsAccountMenuOpen), source: viewModel));
        SetBinding(
            IsSettingsSectionProperty,
            new Binding(nameof(ShellViewModel.IsSettingsSection), source: viewModel));
        ConnectionStatusLabel.SetBinding(
            Label.TextProperty,
            new Binding(nameof(ShellViewModel.ConnectionStatus), source: viewModel));
        ConnectionStatusBorder.SetBinding(
            IsVisibleProperty,
            new Binding(nameof(ShellViewModel.ShowConnectionStatus), source: viewModel));
    }

    private void OnPinClicked(object? sender, EventArgs eventArgs)
    {
        _windowShellAdapter.TogglePinned();
        IsPinned = _windowShellAdapter.IsPinned;
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.Dispatch(() => IsPinned = _windowShellAdapter.IsPinned);
}
