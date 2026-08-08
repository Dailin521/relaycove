using System.Windows;
using System.Windows.Media;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientThemeResourceTests
{
    [Fact]
    public async Task ClientTheme_WhenLoaded_ContainsExactRc25SemanticColors()
    {
        await RunOnStaAsync(() =>
        {
            var resources = LoadDictionary("ClientTheme.xaml");

            AssertBrush(resources, "RcPrimaryBrush", "#FF1677D2");
            AssertBrush(resources, "RcPrimaryHoverBrush", "#FF0958D9");
            AssertBrush(resources, "RcPrimaryPressedBrush", "#FF003EB3");
            AssertBrush(resources, "RcPrimarySoftBrush", "#FFEAF3FF");
            AssertBrush(resources, "RcCanvasBrush", "#FFF5F7FA");
            AssertBrush(resources, "RcSurfaceBrush", "#FFFFFFFF");
            AssertBrush(resources, "RcBorderBrush", "#FFE5EAF0");
            AssertBrush(resources, "RcTextPrimaryBrush", "#FF1F2328");
            AssertBrush(resources, "RcTextSecondaryBrush", "#FF667085");
            AssertBrush(resources, "RcDangerBrush", "#FFD92D20");
            AssertBrush(resources, "RcSuccessBrush", "#FF12B76A");
            AssertBrush(resources, "RcWarningBrush", "#FFF79009");
        });
    }

    [Fact]
    public async Task ClientTheme_WhenMerged_ResolvesEveryRequiredDictionary()
    {
        await RunOnStaAsync(() =>
        {
            var applicationResources = new ResourceDictionary();

            foreach (var fileName in new[]
                     {
                         "ClientTheme.xaml",
                         "ClientIcons.xaml",
                         "ClientControls.xaml",
                     })
            {
                applicationResources.MergedDictionaries.Add(LoadDictionary(fileName));
            }

            Assert.Equal(3, applicationResources.MergedDictionaries.Count);
            Assert.IsType<SolidColorBrush>(applicationResources["RcPrimaryBrush"]);
            Assert.IsAssignableFrom<Geometry>(applicationResources["RcIconChat"]);
            Assert.IsType<Style>(applicationResources["RcPrimaryButtonStyle"]);
        });
    }

    [Fact]
    public async Task ClientIcons_WhenLoaded_ContainsEveryRequiredVectorIcon()
    {
        await RunOnStaAsync(() =>
        {
            var resources = LoadDictionary("ClientIcons.xaml");
            var requiredKeys = new[]
            {
                "RcIconChat",
                "RcIconContacts",
                "RcIconChannels",
                "RcIconBell",
                "RcIconFolder",
                "RcIconSettings",
                "RcIconMore",
                "RcIconSearch",
                "RcIconMembers",
                "RcIconPin",
                "RcIconAttach",
                "RcIconMention",
                "RcIconSmile",
                "RcIconMicrophone",
                "RcIconScissors",
                "RcIconSend",
                "RcIconReply",
                "RcIconCopy",
                "RcIconRetry",
                "RcIconMinimize",
                "RcIconMaximize",
                "RcIconRestore",
                "RcIconClose",
            };

            foreach (var key in requiredKeys)
            {
                Assert.IsAssignableFrom<Geometry>(resources[key]);
            }
        });
    }

    private static ResourceDictionary LoadDictionary(string fileName)
    {
        _ = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/RelayCove.Client;component/Resources/{fileName}",
                UriKind.Absolute),
        };
    }

    private static void AssertBrush(
        ResourceDictionary resources,
        string key,
        string expectedColor)
    {
        var brush = Assert.IsType<SolidColorBrush>(resources[key]);
        var expected = (Color)ColorConverter.ConvertFromString(expectedColor);
        Assert.Equal(expected, brush.Color);
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
