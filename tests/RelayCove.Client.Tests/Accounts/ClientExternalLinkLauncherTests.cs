using System.ComponentModel;
using System.Diagnostics;
using RelayCove.Client.Accounts;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientExternalLinkLauncherTests
{
    [Fact]
    public void TryOpen_WhenLinkIsSafe_UsesOnlyShellExecuteFileName()
    {
        ProcessStartInfo? captured = null;
        var link = new ClientMessageLinkPresentation(
            "https://Example.test/a?q=1",
            "https://example.test/a?q=1");

        var opened = ClientExternalLinkLauncher.TryOpen(
            link,
            startInfo => captured = startInfo);

        Assert.True(opened);
        Assert.NotNull(captured);
        Assert.Equal("https://example.test/a?q=1", captured.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Equal(string.Empty, captured.Arguments);
        Assert.Empty(captured.ArgumentList);
        Assert.Equal(string.Empty, captured.Verb);
        Assert.Equal(string.Empty, captured.WorkingDirectory);
        Assert.False(captured.ErrorDialog);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("mailto:user@example.test")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test\\@evil.test/")]
    public void TryOpen_WhenAbsoluteUriIsUnsafe_DoesNotInvokeShell(string absoluteUri)
    {
        var invoked = false;
        var link = new ClientMessageLinkPresentation(absoluteUri, absoluteUri);

        var opened = ClientExternalLinkLauncher.TryOpen(
            link,
            _ => invoked = true);

        Assert.False(opened);
        Assert.False(invoked);
    }

    [Fact]
    public void TryOpen_WhenShellAssociationFails_ReturnsFalse()
    {
        var link = new ClientMessageLinkPresentation(
            "https://example.test/",
            "https://example.test/");

        var opened = ClientExternalLinkLauncher.TryOpen(
            link,
            _ => throw new Win32Exception("no association"));

        Assert.False(opened);
    }

    [Fact]
    public void TryOpen_WhenUnexpectedFailureOccurs_DoesNotHideIt()
    {
        var link = new ClientMessageLinkPresentation(
            "https://example.test/",
            "https://example.test/");

        Assert.Throws<ApplicationException>(() =>
            ClientExternalLinkLauncher.TryOpen(
                link,
                _ => throw new ApplicationException("unexpected")));
    }
}
