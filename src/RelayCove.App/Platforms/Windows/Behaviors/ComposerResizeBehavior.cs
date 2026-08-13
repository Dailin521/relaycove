using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerResizeBehavior : PlatformBehavior<Button, Microsoft.UI.Xaml.Controls.Button>
{
    private const double MinimumHeight = 72d;
    private const double MaximumHeight = 300d;
    private const double KeyboardStep = 16d;
    private readonly PanGestureRecognizer _panGesture = new();
    private double _panStartHeight;

    public static readonly BindableProperty HeightProperty = BindableProperty.Create(
        nameof(Height),
        typeof(double),
        typeof(ComposerResizeBehavior),
        112d,
        BindingMode.TwoWay,
        coerceValue: static (_, value) => Clamp((double)value));

    public ComposerResizeBehavior()
    {
        _panGesture.PanUpdated += OnPanUpdated;
    }

    public double Height
    {
        get => (double)GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    protected override void OnAttachedTo(Button bindable, Microsoft.UI.Xaml.Controls.Button platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        bindable.GestureRecognizers.Add(_panGesture);
        platformView.KeyDown += OnKeyDown;
    }

    protected override void OnDetachedFrom(Button bindable, Microsoft.UI.Xaml.Controls.Button platformView)
    {
        platformView.KeyDown -= OnKeyDown;
        bindable.GestureRecognizers.Remove(_panGesture);
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs eventArgs)
    {
        switch (eventArgs.StatusType)
        {
            case GestureStatus.Started:
                _panStartHeight = Height;
                break;
            case GestureStatus.Running:
                Height = Clamp(_panStartHeight - eventArgs.TotalY);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        var next = eventArgs.Key switch
        {
            VirtualKey.Up => Height + KeyboardStep,
            VirtualKey.Down => Height - KeyboardStep,
            VirtualKey.Home => MinimumHeight,
            VirtualKey.End => MaximumHeight,
            _ => double.NaN
        };

        if (!double.IsFinite(next)) return;
        Height = Clamp(next);
        eventArgs.Handled = true;
    }

    private static double Clamp(double value) => Math.Clamp(value, MinimumHeight, MaximumHeight);
}
