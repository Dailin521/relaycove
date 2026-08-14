using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelayCove.App.Controls;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;
using RelayCove.Data;
using RelayCove.Zulip.Client;

namespace RelayCove.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<ComposerResizeHandle, ComposerResizeHandleHandler>());

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ISecureKeyValueStore, MauiSecureKeyValueStore>();
        builder.Services.AddSingleton<ICredentialVault, SecureStorageCredentialVault>();
        builder.Services.AddSingleton<ILastRealmStore, PreferencesLastRealmStore>();
        builder.Services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        builder.Services.AddSingleton<IAppearanceService, MauiAppearanceService>();
        builder.Services.AddSingleton<IUiPreferencesService, MauiUiPreferencesService>();
        builder.Services.AddSingleton<IPlatformInteractionService, MauiPlatformInteractionService>();
        builder.Services.AddSingleton<IFileSelectionService, MauiFileSelectionService>();
        builder.Services.AddSingleton<IRealmMediaService, RealmMediaService>();
        builder.Services.AddSingleton<IFileSaveService, WindowsFileSaveService>();
        builder.Services.AddSingleton<IWindowShellAdapter, WindowsWindowShellAdapter>();
        builder.Services.AddSingleton<IAccountStore>(_ => new SqliteAccountStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<IZulipGateway, ZulipGateway>();
#if DEBUG
        if (NativeShellPreviewSession.IsRequested)
            builder.Services.AddSingleton<IClientSession, NativeShellPreviewSession>();
        else
#endif
            builder.Services.AddSingleton<IClientSession, ClientSession>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<ProductBarView>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
