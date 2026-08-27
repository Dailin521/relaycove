using System.Text.RegularExpressions;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

internal static class SearchContentClassifier
{
    private static readonly Regex MarkdownLink = new(
        "!?\\[(?<name>[^]\\r\\n]{1,256})\\]\\((?<url>[^)\\r\\n]{1,4096})\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BareUrl = new(
        "https?://[^\\s<>()]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".gif", ".jpeg", ".jpg", ".png", ".webp"
    };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".webm", ".wmv"
    };

    public static SearchContentKind Classify(string content, RealmEndpoint? realm)
    {
        var kinds = SearchContentKind.Message;
        MessageQuote.ParseLeadingSequence(content, out var source);
        foreach (Match match in MarkdownLink.Matches(source))
        {
            kinds |= ClassifyUrl(match.Groups["url"].Value, realm);
        }
        foreach (Match match in BareUrl.Matches(source))
        {
            kinds |= ClassifyUrl(match.Value, realm);
        }
        return kinds;
    }

    private static SearchContentKind ClassifyUrl(string rawValue, RealmEndpoint? realm)
    {
        var value = rawValue.Trim().Trim('<', '>').TrimEnd('.', ',', ';', ':', '!', '?');
        if (!TryResolve(value, realm, out var uri)) return 0;

        var extension = Path.GetExtension(uri.AbsolutePath);
        var isImage = ImageExtensions.Contains(extension);
        var isVideo = VideoExtensions.Contains(extension);
        var isUpload = realm is not null &&
                       string.Equals(uri.Scheme, realm.Uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(uri.Host, realm.Uri.Host, StringComparison.OrdinalIgnoreCase) &&
                       uri.Port == realm.Uri.Port &&
                       uri.AbsolutePath.StartsWith("/user_uploads/", StringComparison.Ordinal) &&
                       !uri.AbsolutePath.StartsWith("/user_uploads/temporary/", StringComparison.Ordinal);

        var kinds = isImage
            ? SearchContentKind.Image
            : isVideo
                ? SearchContentKind.Video
                : isUpload
                    ? SearchContentKind.File
                    : SearchContentKind.Link;
        if (!isUpload && (isImage || isVideo)) kinds |= SearchContentKind.Link;
        return kinds;
    }

    private static bool TryResolve(string value, RealmEndpoint? realm, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            uri.Scheme is "http" or "https")
        {
            return true;
        }
        return realm is not null &&
               Uri.TryCreate(realm.Uri, value, out uri!) &&
               uri.Scheme is "http" or "https";
    }
}
