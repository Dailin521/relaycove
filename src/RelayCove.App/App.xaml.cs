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
    private int _shutdownStarted;

    public App(IServiceProvider services, IWindowShellAdapter windowShellAdapter)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
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
        window.Destroying += (_, _) => Shutdown();
        return window;
    }

    private void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        try
        {
            StopAndDisposeServicesAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Window teardown is best effort and never records session data.
        }
    }

    private async Task StopAndDisposeServicesAsync()
    {
        var session = _services.GetRequiredService<IClientSession>();
        try
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        if (session is IAsyncDisposable sessionDisposable)
        {
            try
            {
                await sessionDisposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _services.GetService<ShellViewModel>()?.Dispose();
        if (_services.GetService<IAccountStore>() is IAsyncDisposable storeDisposable)
        {
            try
            {
                await storeDisposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        if (_services.GetService<IZulipGateway>() is IDisposable gatewayDisposable)
        {
            try
            {
                gatewayDisposable.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
