using System.Text.RegularExpressions;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record MessageContentPresentation(
    string Body,
    IReadOnlyList<MessageQuote> Quotes,
    IReadOnlyList<MessageAttachmentItem> Attachments)
{
    public IReadOnlyList<MessageTextRun> BodyRuns { get; init; } = [];
    private const string MarkdownLinkPattern =
        @"(?<image>!)?\[(?<name>(?:\\.|[^\]\\\r\n]){0,256})\]\((?<url>(?:\\.|[^)\\\r\n]){1,4096})\)";
    private static readonly Regex MarkdownLink = new(
        MarkdownLinkPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReadableMarkdown = new(
        @"^(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n(?<code>[\s\S]*?)^\k<fence>[ \t]*\r?$" +
        @"|(?<ticks>`+)(?<inline>[^`\r\n]+)\k<ticks>" +
        @"|\\(?<escaped>[\\`*{}\[\]()#+\-.!_>~|])" +
        "|" + MarkdownLinkPattern +
        @"|@_?\*\*(?<mention>[^*\r\n|]+)\|\d+\*\*" +
        @"|(?<![\p{L}\p{N}\\])(?<emphasis>\*\*|__|~~|\*|_)(?=\S)(?<text>[^\r\n]+?)(?<=\S)\k<emphasis>(?![\p{L}\p{N}])" +
        @"|^(?:[ \t]*>[ \t]?)+|^#{1,6}[ \t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".gif", ".jpeg", ".jpg", ".png", ".webp"
    };

    public static MessageContentPresentation Parse(
        string content, RealmEndpoint? realm, IReadOnlyDictionary<string, RealmEmoji>? realmEmojis = null)
    {
        var quotes = MessageQuote.ParseLeadingSequence(content, out var source)
            .Select(quote =>
            {
                var quotedAttachments = new List<MessageAttachmentItem>();
                return quote with
                {
                    Body = ToPlainText(quote.Body, realm, quotedAttachments),
                    Attachments = quotedAttachments
                };
            })
            .ToArray();
        if (realm is null)
        {
            return Create(source, quotes, [], realmEmojis);
        }
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
            var isImage = IsImageAttachment(name, resolved.AbsolutePath);
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
        return Create(body, quotes, attachments, realmEmojis);
    }

    private static MessageContentPresentation Create(string body, IReadOnlyList<MessageQuote> quotes,
        IReadOnlyList<MessageAttachmentItem> attachments, IReadOnlyDictionary<string, RealmEmoji>? realmEmojis)
    {
        var runs = EmojiShortcodeCatalog.CreateRuns(body, realmEmojis);
        return new MessageContentPresentation(string.Concat(runs.Select(run => run.Text)), quotes, attachments)
        {
            BodyRuns = runs
        };
    }

    // Display projection only. Copying, editing and quoting still use MessageItem.Content.
    internal static string ToPlainText(string content, RealmEndpoint? realm,
        IReadOnlyDictionary<string, RealmEmoji>? realmEmojis = null) => ToPlainText(content, realm, null, realmEmojis);

    private static string ToPlainText(
        string content, RealmEndpoint? realm, List<MessageAttachmentItem>? quotedAttachments,
        IReadOnlyDictionary<string, RealmEmoji>? realmEmojis = null)
    {
        var parts = new List<string>();
        var pending = new Stack<string>();
        pending.Push(content);
        while (pending.TryPop(out var source))
        {
            var quotes = MessageQuote.ParseLeadingSequence(source, out var remainder);
            if (quotes.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(remainder)) pending.Push(remainder);
                for (var index = quotes.Count - 1; index >= 0; index--) pending.Push(quotes[index].Body);
                continue;
            }

            var text = string.Concat(EmojiShortcodeCatalog.CreateRuns(source, realmEmojis).Select(run => run.Text));
            text = ReadableMarkdown.Replace(text, match =>
            {
                if (match.Groups["code"].Success) return match.Groups["code"].Value.TrimEnd('\r', '\n');
                if (match.Groups["inline"].Success) return match.Groups["inline"].Value;
                if (match.Groups["escaped"].Success) return match.Groups["escaped"].Value;
                if (match.Groups["mention"].Success) return match.Groups["mention"].Value;
                if (match.Groups["text"].Success) return ToPlainText(match.Groups["text"].Value, realm, quotedAttachments, realmEmojis);
                if (!match.Groups["url"].Success) return string.Empty;

                var name = match.Groups["name"].Value;
                if (quotedAttachments is not null)
                {
                    var readableName = ToPlainText(name, realm, realmEmojis);
                    if (string.IsNullOrWhiteSpace(readableName)) readableName = "附件";
                    if (realm is null || !TryResolveUpload(realm, match.Groups["url"].Value.Trim('<', '>'), out var upload))
                        return readableName;
                    if (quotedAttachments.Any(attachment => attachment.SourceUrl == upload.AbsoluteUri)) return string.Empty;
                    var isImage = IsImageAttachment(readableName, upload.AbsolutePath);
                    if (quotedAttachments.Count >= 10 || isImage && quotedAttachments.Count(attachment => attachment.IsImage) >= 4)
                        return readableName;
                    quotedAttachments.Add(new MessageAttachmentItem(isImage ? "image" : "file", readableName, upload.AbsoluteUri));
                    return string.Empty;
                }
                if (match.Groups["image"].Success) return "[图片]";
                var url = match.Groups["url"].Value.Trim('<', '>');
                if (realm is not null && TryResolveUpload(realm, url, out var resolved))
                {
                    return IsImageAttachment(name, resolved.AbsolutePath) ? "[图片]" : "[文件]";
                }
                // Relative cached uploads can be summarized before a Realm is restored.
                if (realm is null && url.StartsWith("/user_uploads/", StringComparison.Ordinal) &&
                    !url.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal))
                {
                    return IsImageAttachment(name, url.Split('?', '#')[0]) ? "[图片]" : "[文件]";
                }
                return ToPlainText(name, realm, realmEmojis);
            }).Trim();
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    private static bool IsImageAttachment(string name, string path)
    {
        var pathName = Uri.UnescapeDataString(Path.GetFileName(path));
        return ImageExtensions.Contains(Path.GetExtension(name)) || ImageExtensions.Contains(Path.GetExtension(pathName));
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
