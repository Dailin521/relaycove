using Windows.System;

namespace RelayCove.App.Platforms.Windows;

internal static class KeyboardInputPolicy
{
    internal static bool ShouldBlock(
        VirtualKey key, bool isTextInput, bool isAltDown, bool isControlDown = false, bool hasSelectedText = false)
    {
        // Window management remains owned by Windows.
        if (key is VirtualKey.LeftWindows or VirtualKey.RightWindows ||
            isAltDown && key is VirtualKey.F4 or VirtualKey.Space) return false;

        // Copy the mouse-selected message text without enabling keyboard navigation.
        if (hasSelectedText && isControlDown && !isAltDown && key == VirtualKey.C) return false;

        // Tab must not move focus out of an editor into lists or other controls.
        return key == VirtualKey.Tab || !isTextInput;
    }
}
