using Microsoft.Extensions.DependencyInjection;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.Preview.NativeTests.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        ProbeLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ProbeLog.Write("domain-unhandled", args.ExceptionObject.ToString());
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception.HResult == unchecked((int)0x80004002))
                ProbeLog.Write("first-chance-E_NOINTERFACE", args.Exception.ToString());
        };
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            ProbeLog.Write("winui-unhandled", $"HRESULT=0x{args.Exception.HResult:X8} {args.Exception}");
            Environment.Exit(2);
        };
    }

    protected override MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<ProbeApplication>();
        builder.ConfigureImageSources(sources =>
        {
            var assembly = typeof(RelayCove.App.Controls.RealmMediaImageView).Assembly;
            var source = assembly.GetType("RelayCove.App.Platforms.Windows.RealmImageSource", true)!;
            var service = assembly.GetType("RelayCove.App.Platforms.Windows.RealmImageSourceService", true)!;
            var add = typeof(MauiApp).Assembly.GetTypes().SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                .Single(method => method.Name == "AddService" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 2
                    && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsInstanceOfType(sources));
            add.MakeGenericMethod(source, service).Invoke(null, [sources]);
        });
        builder.Services.AddSingleton<IRealmMediaService, FixtureMediaService>();
        builder.Services.AddSingleton(System.Reflection.DispatchProxy.Create<IClientSession, FixtureSession>());
        return builder.Build();
    }
}
