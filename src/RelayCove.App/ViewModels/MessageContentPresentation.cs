using System.Text.RegularExpressions;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record MessageContentPresentation(
    string Body,
    string? QuoteSender,
    string? QuoteBody,
    IReadOnlyList<MessageAttachmentItem> Attachments)
{
    private static readonly Regex MarkdownLink = new(
        "!?\\[(?<name>[^]\\r\\n]{1,256})\\]\\((?<url>[^)\\r\\n]{1,4096})\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".gif", ".jpeg", ".jpg", ".png", ".webp"
    };

    public static MessageContentPresentation Parse(string content, RealmEndpoint? realm)
    {
        var quote = MessageQuote.ParseLeading(content);
        var source = quote?.Remainder ?? content;
        if (realm is null) return new MessageContentPresentation(source, quote?.Sender, quote?.Body, []);
        var attachments = new List<MessageAttachmentItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var imageCount = 0;
        var body = MarkdownLink.Replace(source, match =>
        {
            if (!TryResolveUpload(realm, match.Groups["url"].Value.Trim('<', '>'), out var resolved)) return match.Value;
            var rawName = match.Groups["name"].Value;
            var name = Regex.Replace(rawName, "\\\\([\\\\[\\]()])", "$1", RegexOptions.CultureInvariant).Trim();
            if (name.Length == 0) name = "附件";
            if (name.Length > 256) name = name[..256];
            var pathName = Uri.UnescapeDataString(Path.GetFileName(resolved.AbsolutePath));
            var isImage = ImageExtensions.Contains(Path.GetExtension(name)) || ImageExtensions.Contains(Path.GetExtension(pathName));
            if (!seen.Contains(resolved.AbsoluteUri) && attachments.Count >= 10) return match.Value;
            if (!seen.Contains(resolved.AbsoluteUri) && isImage && imageCount >= 4) return match.Value;
            if (seen.Add(resolved.AbsoluteUri))
            {
                attachments.Add(new MessageAttachmentItem(isImage ? "image" : "file", name, resolved.AbsoluteUri));
                if (isImage) imageCount++;
            }
            return string.Empty;
        });
        body = Regex.Replace(body, "[ \\t]+\\n", "\n", RegexOptions.CultureInvariant);
        body = Regex.Replace(body, "\\n{3,}", "\n\n", RegexOptions.CultureInvariant).Trim();
        return new MessageContentPresentation(body, quote?.Sender, quote?.Body, attachments);
    }

    private static bool TryResolveUpload(RealmEndpoint realm, string value, out Uri result)
    {
        result = null!;
        if (!Uri.TryCreate(realm.Uri, value, out var resolved) ||
            !string.Equals(resolved.Scheme, realm.Uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, realm.Uri.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != realm.Uri.Port ||
            !resolved.AbsolutePath.StartsWith("/user_uploads/", StringComparison.Ordinal) ||
            resolved.AbsolutePath.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal))
        {
            return false;
        }
        result = resolved;
        return true;
    }
}
