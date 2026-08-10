using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ISecureKeyValueStore, MauiSecureKeyValueStore>();
        builder.Services.AddSingleton<ICredentialVault, SecureStorageCredentialVault>();
        builder.Services.AddSingleton<ILastRealmStore, PreferencesLastRealmStore>();
        builder.Services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        builder.Services.AddSingleton<IAccountStore>(_ => new SqliteAccountStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<IZulipGateway, ZulipGateway>();
        builder.Services.AddSingleton<IClientSession, ClientSession>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
