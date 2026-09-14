using RelayCove.App.Platforms.Windows;
using Windows.System;

namespace RelayCove.App.Tests;

public sealed class KeyboardInputPolicyTests
{
    [Theory]
    [InlineData(VirtualKey.C, true, false, true, false)]
    [InlineData(VirtualKey.C, true, false, false, true)]
    [InlineData(VirtualKey.C, false, false, true, true)]
    [InlineData(VirtualKey.C, true, true, true, true)]
    [InlineData(VirtualKey.A, true, false, true, true)]
    [InlineData(VirtualKey.X, true, false, true, true)]
    [InlineData(VirtualKey.V, true, false, true, true)]
    [InlineData(VirtualKey.Left, true, false, true, true)]
    [InlineData(VirtualKey.Down, false, false, true, true)]
    [InlineData(VirtualKey.Tab, false, false, true, true)]
    public void ShouldBlock_WhenReadOnlyTextHasFocus_AllowsOnlyCopyingExistingSelection(
        VirtualKey key, bool control, bool alt, bool hasSelection, bool expected)
    {
        Assert.Equal(expected, KeyboardInputPolicy.ShouldBlock(
            key, isTextInput: false, isAltDown: alt, isControlDown: control, hasSelectedText: hasSelection));
    }

    [Theory]
    [InlineData(VirtualKey.Up)]
    [InlineData(VirtualKey.Down)]
    [InlineData(VirtualKey.Left)]
    [InlineData(VirtualKey.Right)]
    [InlineData(VirtualKey.Home)]
    [InlineData(VirtualKey.End)]
    [InlineData(VirtualKey.PageUp)]
    [InlineData(VirtualKey.PageDown)]
    [InlineData(VirtualKey.Tab)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Space)]
    [InlineData(VirtualKey.Escape)]
    [InlineData(VirtualKey.Application)]
    [InlineData(VirtualKey.F10)]
    [InlineData(VirtualKey.A)]
    public void ShouldBlock_WhenListMenuOrOtherNonEditorHasFocus_PreventsNavigationAndActivation(VirtualKey key)
    {
        Assert.True(KeyboardInputPolicy.ShouldBlock(key, isTextInput: false, isAltDown: false));
    }

    [Theory]
    [InlineData(VirtualKey.Up)]
    [InlineData(VirtualKey.Down)]
    [InlineData(VirtualKey.Left)]
    [InlineData(VirtualKey.Right)]
    [InlineData(VirtualKey.Home)]
    [InlineData(VirtualKey.End)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Space)]
    [InlineData(VirtualKey.Escape)]
    [InlineData(VirtualKey.Back)]
    [InlineData(VirtualKey.Delete)]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.C)]
    [InlineData(VirtualKey.V)]
    [InlineData(VirtualKey.X)]
    [InlineData(VirtualKey.Z)]
    [InlineData(VirtualKey.Shift)]
    [InlineData(VirtualKey.Control)]
    public void ShouldBlock_WhenEditorHasFocus_PreservesTypingSelectionClipboardAndIme(VirtualKey key)
    {
        Assert.False(KeyboardInputPolicy.ShouldBlock(key, isTextInput: true, isAltDown: false));
    }

    [Fact]
    public void ShouldBlock_WhenTabPressedInEditor_DoesNotMoveFocusToOtherControls()
    {
        Assert.True(KeyboardInputPolicy.ShouldBlock(VirtualKey.Tab, isTextInput: true, isAltDown: false));
    }

    [Theory]
    [InlineData(VirtualKey.F4, true)]
    [InlineData(VirtualKey.Space, true)]
    [InlineData(VirtualKey.LeftWindows, false)]
    [InlineData(VirtualKey.RightWindows, false)]
    public void ShouldBlock_WhenWindowsShortcutPressed_LeavesWindowManagementToWindows(VirtualKey key, bool isAltDown)
    {
        Assert.False(KeyboardInputPolicy.ShouldBlock(key, isTextInput: false, isAltDown));
    }
}
