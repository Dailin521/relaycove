using System.Runtime.InteropServices;
using RelayCove.Client.Accounts;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientClipboardWriterTests
{
    [Fact]
    public void TryWrite_WhenWriterSucceeds_PreservesExactContent()
    {
        const string content = "  first line\r\nsecond line  ";
        string? captured = null;

        var copied = ClientClipboardWriter.TryWrite(
            content,
            value => captured = value);

        Assert.True(copied);
        Assert.Equal(content, captured);
    }

    [Fact]
    public void TryWrite_WhenClipboardIsTemporarilyUnavailable_ReturnsFalse()
    {
        var copied = ClientClipboardWriter.TryWrite(
            "content",
            _ => throw new ExternalException("clipboard busy"));

        Assert.False(copied);
    }

    [Fact]
    public void TryWrite_WhenUnexpectedFailureOccurs_DoesNotHideIt()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClientClipboardWriter.TryWrite(
                "content",
                _ => throw new InvalidOperationException("unexpected")));
    }
}
