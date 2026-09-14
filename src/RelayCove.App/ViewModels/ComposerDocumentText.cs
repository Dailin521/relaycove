using System.Text;

namespace RelayCove.App.ViewModels;

// Native images occupy one document position; drafts and server sends keep
// their complete shortcode. Offsets always remain UTF-16, as in RichEditBox.
internal sealed class ComposerDocumentText(string text, IReadOnlyDictionary<int, string> images)
{
    internal string NativeText => text;
    internal string RawText => Expand(text.Length);

    internal int ToTextIndex(int documentIndex) => Expand(Math.Clamp(documentIndex, 0, text.Length)).Length;

    internal int ToDocumentIndex(int textIndex)
    {
        var remaining = Math.Max(0, textIndex);
        for (var index = 0; index < text.Length; index++)
        {
            if (remaining == 0) return index;
            var length = images.TryGetValue(index, out var alternate) ? alternate.Length : text[index] == '\r' ? 2 : 1;
            if (remaining < length) return index;
            remaining -= length;
        }
        return text.Length;
    }

    private string Expand(int end)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < end; index++)
        {
            if (images.TryGetValue(index, out var alternate)) builder.Append(alternate);
            else if (text[index] == '\r') builder.Append("\r\n");
            else builder.Append(text[index]);
        }
        return builder.ToString();
    }
}
