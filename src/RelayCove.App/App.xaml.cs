using Microsoft.Extensions.DependencyInjection;
using RelayCove.App.Controls;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly IWindowShellAdapter _windowShellAdapter;
    private readonly IAppNotificationService _appNotificationService;
    private readonly IApplicationShutdownCoordinator _shutdownCoordinator;

    public App(
        IServiceProvider services,
        IWindowShellAdapter windowShellAdapter,
        IAppNotificationService appNotificationService,
        IApplicationShutdownCoordinator shutdownCoordinator)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
        _appNotificationService = appNotificationService ?? throw new ArgumentNullException(nameof(appNotificationService));
        _shutdownCoordinator = shutdownCoordinator ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var viewModel = _services.GetRequiredService<ShellViewModel>();
        var titleBar = _services.GetRequiredService<ProductBarView>();
        titleBar.Bind(viewModel);
        var window = new Window(_services.GetRequiredService<MainPage>())
        {
            TitleBar = titleBar
        };
        _windowShellAdapter.Attach(window);
        _appNotificationService.Attach(window);
        window.Destroying += (_, _) => _ = _shutdownCoordinator.RequestShutdownAsync(ApplicationShutdownEntryPoint.WindowDestroyed);
        return window;
    }
}
