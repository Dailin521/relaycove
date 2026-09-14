using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace RelayCove.App.Platforms.Windows;

internal static class MouseOnlyNavigation
{
    internal static void Enable()
    {
        FocusManager.GettingFocus -= OnGettingFocus;
        FocusManager.GettingFocus += OnGettingFocus;
    }

    private static void OnGettingFocus(object? sender, GettingFocusEventArgs args)
    {
        if (args.FocusState == FocusState.Keyboard && !IsTextInput(args.NewFocusedElement))
            args.Cancel = true;

        // Native flyouts have a separate visual root from MainPage. Attach to
        // whichever root is receiving focus, including list item containers.
        UIElement? root = null;
        for (var current = args.NewFocusedElement; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element) root = element;
        }
        if (root is null) return;
        root.PreviewKeyDown -= OnPreviewKey;
        root.PreviewKeyUp -= OnPreviewKey;
        root.PreviewKeyDown += OnPreviewKey;
        root.PreviewKeyUp += OnPreviewKey;
    }

    private static void OnPreviewKey(object sender, KeyRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        var isControlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        if (KeyboardInputPolicy.ShouldBlock(args.Key,
            IsTextInput(source), args.KeyStatus.IsMenuKeyDown, isControlDown, HasSelectedText(source)))
        {
            args.Handled = true;
        }
    }

    private static bool HasSelectedText(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBlock { IsTextSelectionEnabled: true } text && !string.IsNullOrEmpty(text.SelectedText))
                return true;
            if (current is RichTextBlock { IsTextSelectionEnabled: true } richText && !string.IsNullOrEmpty(richText.SelectedText))
                return true;
        }
        return false;
    }

    private static bool IsTextInput(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox { IsReadOnly: false } or RichEditBox { IsReadOnly: false } or PasswordBox)
                return true;
        }
        return false;
    }
}
