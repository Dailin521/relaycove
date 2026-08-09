using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RelayCove.Client.Controls;

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
            AssertBrush(resources, "RcNeutralBrush", "#FF8F9AA5");
            AssertBrush(resources, "RcDangerBrush", "#FFD92D20");
            AssertBrush(resources, "RcSuccessBrush", "#FF12B76A");
            AssertBrush(resources, "RcWarningBrush", "#FFF79009");
        });
    }

    [Fact]
    public async Task ClientTheme_WhenLoaded_ContainsNamedInputAndScrollBarResources()
    {
        await RunOnStaAsync(() =>
        {
            var resources = LoadDictionary("ClientTheme.xaml");

            AssertBrush(resources, "RcFocusRingBrush", "#FF1677D2");
            AssertBrush(resources, "RcPrimaryPressedSoftBrush", "#FFD8EAFE");
            AssertBrush(resources, "RcSurfaceHoverBrush", "#FFF2F7FD");
            AssertBrush(resources, "RcScrollBarTrackBrush", "#FFEAF0F6");
            AssertBrush(resources, "RcScrollBarThumbBrush", "#FFB8C3CF");
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
    public async Task ClientControls_WhenMerged_ProvidesFocusableNamedControlStyles()
    {
        await RunOnStaAsync(() =>
        {
            var resources = LoadMergedThemeAndControlResources();
            var focusStyle = Assert.IsType<Style>(resources["RcFocusVisualStyle"]);

            Assert.Equal(typeof(Control), focusStyle.TargetType);
            AssertFocusableStyle(resources, "RcButtonStyle", typeof(Button));
            AssertFocusableStyle(resources, "RcToggleButtonStyle", typeof(ToggleButton));
            AssertFocusableStyle(resources, "RcTextBoxStyle", typeof(TextBox));
            AssertFocusableStyle(resources, "RcPasswordBoxStyle", typeof(PasswordBox));
            AssertFocusableStyle(resources, "RcComboBoxStyle", typeof(ComboBox));
            AssertFocusableStyleWithoutTemplate(resources, "RcExpanderStyle", typeof(Expander));
            AssertFocusableStyle(resources, "RcScrollBarStyle", typeof(ScrollBar));
            Assert.Equal(typeof(ComboBoxItem), Assert.IsType<Style>(resources["RcComboBoxItemStyle"]).TargetType);
            Assert.Equal(typeof(Thumb), Assert.IsType<Style>(resources["RcScrollBarThumbStyle"]).TargetType);
            Assert.Equal(
                typeof(RepeatButton),
                Assert.IsType<Style>(resources["RcScrollBarRepeatButtonStyle"]).TargetType);
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

    [Fact]
    public async Task PrimaryActions_WhenHoveredOrPressed_KeepWhiteTextOnDarkBlueBackground()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            var settings = new SettingsPanelControl();
            try
            {
                AssertPrimaryButtonStates(
                    Assert.IsType<Style>(window.Resources["PrimaryButtonStyle"]),
                    "#FF0958D9",
                    "#FF003EB3");
                AssertPrimaryButtonStates(
                    Assert.IsType<Style>(settings.Resources["UpdateActionButtonStyle"]),
                    "#FF0958D9",
                    "#FF003EB3");
            }
            finally
            {
                window.Close();
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

    private static ResourceDictionary LoadMergedThemeAndControlResources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(LoadDictionary("ClientTheme.xaml"));
        resources.MergedDictionaries.Add(LoadDictionary("ClientControls.xaml"));
        return resources;
    }

    private static void AssertFocusableStyle(
        ResourceDictionary resources,
        string key,
        Type targetType)
    {
        var style = Assert.IsType<Style>(resources[key]);

        Assert.Equal(targetType, style.TargetType);
        var focusSetter = Assert.Single(style.Setters.OfType<Setter>(), setter =>
            setter.Property == Control.FocusVisualStyleProperty);
        Assert.Same(resources["RcFocusVisualStyle"], focusSetter.Value);
        Assert.IsType<ControlTemplate>(style.Setters.OfType<Setter>().Single(setter =>
            setter.Property == Control.TemplateProperty).Value);
    }

    private static void AssertFocusableStyleWithoutTemplate(
        ResourceDictionary resources,
        string key,
        Type targetType)
    {
        var style = Assert.IsType<Style>(resources[key]);

        Assert.Equal(targetType, style.TargetType);
        var focusSetter = Assert.Single(style.Setters.OfType<Setter>(), setter =>
            setter.Property == Control.FocusVisualStyleProperty);
        Assert.Same(resources["RcFocusVisualStyle"], focusSetter.Value);
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

    private static void AssertPrimaryButtonStates(
        Style style,
        string hoverColor,
        string pressedColor)
    {
        var template = Assert.IsType<ControlTemplate>(style.Setters.OfType<Setter>().Single(setter =>
            setter.Property == Control.TemplateProperty).Value);

        AssertTemplateTriggerColor(template, UIElement.IsMouseOverProperty, hoverColor);
        AssertTemplateTriggerColor(template, ButtonBase.IsPressedProperty, pressedColor);
    }

    private static void AssertTemplateTriggerColor(
        ControlTemplate template,
        DependencyProperty property,
        string expectedColor)
    {
        var trigger = Assert.Single(template.Triggers.OfType<Trigger>(), candidate =>
            candidate.Property == property && Equals(candidate.Value, true));
        var background = Assert.Single(trigger.Setters.OfType<Setter>(), setter =>
            setter.Property == Control.BackgroundProperty);
        var brush = Assert.IsType<SolidColorBrush>(background.Value);

        Assert.Equal((Color)ColorConverter.ConvertFromString(expectedColor), brush.Color);
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
